// Click-through test of the whole doctor story in a real browser (Playwright, Chromium).
//
//   BASE_URL=http://localhost:8080 node tests/e2e/browser_flow.js
//   (needs `npm i playwright` or an environment where require('playwright') works; SHOTS=<folder> saves screenshots)
//
// What it does: the administrator signs in (typing the user name in capitals), adds two doctors and a third who must choose a new
// password; doctor A registers patients, opens the record; doctor B looks A's patient up, adds himself, is sent a shared patient and
// a message; the administrator withdraws B; B is locked out. Every JSON answer from the server is watched for "Failed" answers
// that the story does not expect (a screen the doctor needs but the server refuses would show up here).
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE = (process.env.BASE_URL || 'http://127.0.0.1:5000').replace(/\/$/, '');
const SHOTS = process.env.SHOTS || '';
const PNG_1PX = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
const CHROMIUM = process.env.CHROMIUM_PATH || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined);
const ADMIN_USER = process.env.ADMIN_USER || 'ADMIN';          // capitals on purpose: the user name is not case sensitive
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || '123';
const SUFFIX = Math.random().toString(36).slice(2, 7);

let passed = 0;
const failed = [];
const unexpected = [];      // "Failed" answers nobody asked for
let expectFailures = 0;     // > 0 while the story deliberately provokes refusals

function ok(name, cond, detail) {
  if (cond) { passed++; console.log('  ok    ' + name); }
  else { failed.push(name); console.log('  FAIL  ' + name + (detail ? '   -> ' + String(detail).slice(0, 300) : '')); }
}

async function newSession(browser, label) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  page.label = label;
  page.on('pageerror', e => { unexpected.push(label + ': page error ' + String(e).split('\n')[0]); });
  page.on('dialog', d => d.accept().catch(() => { }));
  page.on('response', async r => {
    const url = r.url();
    if (!/\/api\/|\/Reporting\/|\/DynamicReporting\//.test(url)) return;
    let text = '';
    try { text = await r.text(); } catch (e) { return; }
    if (r.status() >= 500) { unexpected.push(label + ': HTTP ' + r.status() + ' ' + url.replace(BASE, '')); return; }
    const m = text.match(/"Status":\s*"Failed"[\s\S]*?"ErrorMessage":\s*"([^"]*)"|"ErrorMessage":\s*"([^"]*)"[\s\S]*?"Status":\s*"Failed"/);
    if (m && expectFailures === 0) unexpected.push(label + ': ' + url.replace(BASE, '') + ' -> ' + (m[1] || m[2]));
  });
  return page;
}

async function shot(page, name) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name + '.png') });
}

async function signIn(page, user, password) {
  await page.goto(BASE + '/', { waitUntil: 'load', timeout: 120000 });
  await page.fill('#username_id', user);
  await page.fill('#password', password);
  await Promise.all([page.waitForNavigation({ waitUntil: 'load' }).catch(() => null), page.click('#login')]);
  await page.waitForTimeout(6000);
  return /Home\/Index/.test(page.url());
}

// withdraw the doctors this run created (through the administrator's own screens' API), whatever happened before
async function cleanUp(adminPage, usernames) {
  try {
    await adminPage.evaluate(async (names) => {
      const token = Object.keys(localStorage).map(k => localStorage.getItem(k)).find(v => v && v.split('.').length === 3);
      const h = { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token };
      const staff = await fetch('/api/DoctorAdmin/Staff', { headers: h }).then(r => r.json());
      for (const s of staff.Results || []) {
        if (names.indexOf(s.UserName) >= 0 && s.LoginActive) {
          await fetch('/api/DoctorAdmin/Withdraw', { method: 'POST', headers: h, body: JSON.stringify({ UserId: s.UserId, Reason: 'test finished' }) });
        }
      }
    }, usernames);
  } catch (e) { /* the admin page may be gone */ }
}

async function bodyText(page) {
  return (await page.innerText('body')).replace(/\s+/g, ' ');
}

async function waitText(page, text, ms) {
  try { await page.waitForFunction(t => document.body && document.body.innerText.indexOf(t) >= 0, text, { timeout: ms || 15000 }); return true; }
  catch (e) { return false; }
}

async function addDoctor(page, d) {
  await page.click('text=+ Add doctor');
  await page.waitForSelector('input[name="fn"]');
  await page.fill('input[name="fn"]', d.first);
  await page.fill('input[name="ln"]', d.last);
  await page.selectOption('select[name="g"]', d.gender);
  await page.fill('input[name="em"]', d.user + '@example.org');
  await page.selectOption('select[name="dept"]', { label: d.department });
  await page.fill('input[name="spec"]', d.speciality);
  await page.fill('input[name="un"]', d.user);
  const checked = await page.isChecked('input[name="must"]');
  if (checked !== d.mustChange) await page.click('input[name="must"]');
  await page.click('button:has-text("Create login")');
  await page.waitForSelector('.da-credentials', { timeout: 20000 });
  const codes = await page.$$eval('.da-credentials code', els => els.map(e => e.textContent.trim()));
  await page.click('button:has-text("Done")');
  return { user: codes[0], password: codes[1] };
}

let adminPageForCleanUp = null;
const createdUsers = [];

(async () => {
  const browser = await chromium.launch({ executablePath: CHROMIUM, args: ['--no-sandbox', '--disable-gpu'] });
  const docA = { first: 'Anna', last: 'Alpha' + SUFFIX, gender: 'Female', user: 'dr.anna' + SUFFIX, department: 'Cardiology', speciality: 'Interventional cardiology', mustChange: false };
  const docB = { first: 'Ben', last: 'Beta' + SUFFIX, gender: 'Male', user: 'dr.ben' + SUFFIX, department: 'Peaditric', speciality: 'Paediatrics', mustChange: false };
  const docC = { first: 'Cora', last: 'Gamma' + SUFFIX, gender: 'Female', user: 'dr.cora' + SUFFIX, department: 'Dermatology & Cosmatology', speciality: 'Dermatology', mustChange: true };

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nAdministrator');
  const admin = await newSession(browser, 'admin');
  adminPageForCleanUp = admin;
  ok('admin signs in with the user name in capitals', await signIn(admin, ADMIN_USER, ADMIN_PASSWORD));
  ok('the admin lands on Manage Doctors', /#\/DoctorAdmin/.test(admin.url()) && await waitText(admin, 'Manage Doctors'), admin.url());
  await shot(admin, '01-admin-manage-doctors');
  createdUsers.push(docA.user, docB.user, docC.user);
  const credA = await addDoctor(admin, docA);
  ok('a login is issued for doctor A', !!credA.user && credA.password && credA.password.length >= 6, JSON.stringify(credA));
  await shot(admin, '02-admin-add-doctor-done');
  const credB = await addDoctor(admin, docB);
  const credC = await addDoctor(admin, docC);
  await admin.waitForTimeout(800);
  const t1 = await bodyText(admin);
  ok('all three appear in the list with their speciality', t1.includes('Anna Alpha' + SUFFIX) && t1.includes('Interventional cardiology') && t1.includes('Ben Beta' + SUFFIX) && t1.includes('Cora Gamma' + SUFFIX));
  await shot(admin, '03-admin-list');

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nDoctor A');
  const A = await newSession(browser, 'A');
  ok('doctor A signs in (user name in capitals)', await signIn(A, credA.user.toUpperCase(), credA.password));
  ok('A lands on My Patients', /#\/CareTeam\/MyPatients/.test(A.url()) && await waitText(A, 'My Patients'), A.url());
  const menuA = await A.$$eval('.sidebar-menu li, ul.page-sidebar-menu li, nav li', els => els.map(e => e.innerText.trim()).join(' | ')).catch(() => '');
  const textA = await bodyText(A);
  ok("A's menu holds only the doctor screens (no Billing / Accounting / Settings)", !/\bBilling\b|\bAccounting\b|\bSettings\b|ManageDoctors|Pharmacy/.test(textA.slice(0, 500)), textA.slice(0, 300));
  await shot(A, '04-doctorA-my-patients-empty');

  await A.click('text=+ Register new patient');
  await A.waitForSelector('input[name="fn"]');
  await A.fill('input[name="fn"]', 'Pat');
  await A.fill('input[name="ln"]', 'Patient' + SUFFIX);
  await A.selectOption('select[name="gender"]', 'Male');
  await A.fill('input[name="dob"]', '1984-06-15');
  await A.fill('input[name="ph"]', '0712345678');
  await shot(A, '05-doctorA-register-dialog');
  await A.click('button:has-text("Register patient")');
  ok('A registers a patient and sees them in the list', await waitText(A, 'Pat Patient' + SUFFIX, 15000));
  await shot(A, '06-doctorA-my-patients');

  // open the record in the application's own doctor screens
  await A.click('button:has-text("Open record")');
  ok('the patient record opens', await waitText(A, 'Patient Overview', 30000), A.url());
  await A.waitForTimeout(3000);
  const overview = await bodyText(A);
  ok('the record shows this patient', overview.toUpperCase().includes('PAT PATIENT' + SUFFIX.toUpperCase()), overview.slice(0, 200));
  await shot(A, '07-doctorA-patient-record');
  const tabs = ['Clinical/Vitals', 'Clinical/Allergy', 'Clinical/HomeMedication', 'ProblemsMain', 'Orders', 'PatientVisitHistory', 'Clinical/DoctorsNotes',
    'CurrentMedications', 'ClinicalDocuments', 'NotesSummary', 'ScannedImages'];
  for (const t of tabs) {
    await A.evaluate(h => { window.location.hash = h; }, '#/Doctors/PatientOverviewMain/' + t);
    await A.waitForTimeout(2500);
    ok('tab opens for the doctor: ' + t, !/UnAuthorized|not available to doctor|not under your care/i.test(await bodyText(A)) && A.url().includes(t.split('/')[0]), A.url());
  }
  // the Scanned Images tab: upload a scan through the form and see it listed
  await A.evaluate(() => { window.location.hash = '#/Doctors/PatientOverviewMain/ScannedImages'; });
  await A.waitForTimeout(2500);
  await A.selectOption('select[formcontrolname="FileType"]', 'Clinical');
  await A.fill('input#title', 'Chest scan ' + SUFFIX);
  await A.fill('textarea#title', 'uploaded by the browser test');
  await A.setInputFiles('input[type="file"]', { name: 'scan.png', mimeType: 'image/png', buffer: Buffer.from(PNG_1PX, 'base64') });
  await A.click('input[value="Upload"]');
  ok('a scanned image can be uploaded and is listed', await waitText(A, 'Clinical : Chest scan ' + SUFFIX, 20000));
  await shot(A, '08-doctorA-scanned-images');

  // a lab order signed from the Orders tab (the server saves it as a provisional bill of the visit)
  await A.evaluate(() => { window.location.hash = '#/Doctors/PatientOverviewMain/Orders'; });
  await A.waitForTimeout(3000);
  await A.locator('select').first().selectOption({ label: 'Labs' });
  await A.waitForTimeout(1200);
  await A.fill('input[placeholder="search order items"]', 'Sugar');
  await A.waitForTimeout(2500);
  await A.locator('text=Sugar Fasting').first().click();
  await A.click('button:has-text("Proceed")');
  await A.waitForTimeout(3000);
  await A.click('button:has-text("Sign")');
  ok('a lab order can be placed and signed', await waitText(A, 'order add successfully', 20000));
  await shot(A, '08a-doctorA-lab-order');
  await shot(A, '08-doctorA-orders-tab');

  // a real clinical write through the application's own vitals form
  await A.evaluate(() => { window.location.hash = '#/Doctors/PatientOverviewMain/Clinical/Vitals'; });
  await A.waitForTimeout(3000);
  if (!(await A.locator('input[placeholder="BPSystolic"]').isVisible().catch(() => false))) await A.click('text=New Vitals').catch(() => { });
  await A.waitForSelector('input[placeholder="BPSystolic"]', { timeout: 15000 });
  await A.locator('xpath=//label[contains(.,"Height")]/following::input[1]').fill('172');
  await A.locator('xpath=//label[contains(.,"Weight")]/following::input[1]').fill('68');
  await A.locator('xpath=//label[contains(.,"Temperature")]/following::input[1]').fill('98.4');
  await A.locator('xpath=//label[contains(.,"Pulse")]/following::input[1]').fill('74');
  await A.fill('input[placeholder="BPSystolic"]', '118');
  await A.fill('input[placeholder="BPDiastolic"]', '76');
  await shot(A, '08b-doctorA-vitals-form');
  await A.click('button:has-text("Save")');
  ok('the doctor records vitals and they appear in the list', await waitText(A, '172', 15000) && await waitText(A, '118', 3000), (await bodyText(A)).slice(0, 300));
  await shot(A, '08c-doctorA-vitals-saved');

  // "Home" in the record leads back to the doctor's own list
  await A.click('a.btn-back');
  ok('the Home button of the record returns to My Patients', await waitText(A, 'Register new patient', 15000) && /CareTeam\/MyPatients/.test(A.url()), A.url());
  ok('and the previous patient is no longer shown in the top bar', !(await A.locator('.patient-info-header').isVisible().catch(() => false)));
  await A.waitForTimeout(500);

  // a second patient
  await A.click('text=+ Register new patient');
  await A.waitForSelector('input[name="fn"]');
  await A.fill('input[name="fn"]', 'Sam');
  await A.fill('input[name="ln"]', 'Shared' + SUFFIX);
  await A.selectOption('select[name="gender"]', 'Female');
  await A.fill('input[name="dob"]', '2016-02-29');
  await A.click('button:has-text("Register patient")');
  ok('A registers a second patient', await waitText(A, 'Sam Shared' + SUFFIX, 15000));

  // the application's own Doctor screens open for a doctor (their lists are short for a new doctor, but nothing may error)
  for (const [route, label] of [['Doctors/OutPatientDoctor', 'Out Patient'], ['Doctors/OutPatientDoctor/NewPatient', 'New Patient'],
                                ['Doctors/OutPatientDoctor/OPDRecord', 'OPD Record'], ['Doctors/InPatientDepartment', 'In Patient Department'],
                                ['Doctors/PatientRecord', 'Patient Record']]) {
    await A.evaluate(h => { window.location.hash = h; }, '#/' + route);
    await A.waitForTimeout(3500);
    ok('the Doctor screen opens: ' + label, !/UnAuthorized|not available to doctor|not under your care/i.test(await bodyText(A)) && A.url().includes(route.split('/')[1]), A.url());
    await shot(A, '08d-doctor-screen-' + label.replace(/ /g, '-'));
  }
  await A.evaluate(() => { window.location.hash = '#/CareTeam/MyPatients'; });
  await A.waitForTimeout(1500);

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nDoctor B');
  const B = await newSession(browser, 'B');
  ok('doctor B signs in', await signIn(B, credB.user, credB.password));
  ok("B's list starts empty", await waitText(B, 'No patients on your list yet', 15000));
  await B.click('.page-breadcrumb a:has-text("Find Patient")');
  await B.waitForSelector('input[placeholder^="Patient name"]');
  await B.fill('input[placeholder^="Patient name"]', 'Patient' + SUFFIX);
  await B.press('input[placeholder^="Patient name"]', 'Enter');
  ok("B finds A's patient and sees who the patient is under", await waitText(B, 'Pat Patient' + SUFFIX, 15000) && (await bodyText(B)).includes('Anna Alpha' + SUFFIX));
  await shot(B, '09-doctorB-find-patient');
  ok('B is not shown the patient\'s phone number or address', !(await bodyText(B)).includes('0712345678'));
  expectFailures++;      // the next step is meant to be refused
  await B.evaluate(async () => {   // B tries to read the record directly through the API
    const token = localStorage.getItem('loginToken') || Object.keys(localStorage).map(k => localStorage.getItem(k)).find(v => v && v.split('.').length === 3);
    window.__refused = await fetch('/api/Clinical/PatientAllergies?patientId=1', { headers: { Authorization: 'Bearer ' + token } }).then(r => r.text());
  });
  const refused = await B.evaluate(() => window.__refused);
  ok("B's direct attempt at another doctor's patient is refused", /not under your care/.test(refused || ''), refused);
  expectFailures--;
  await B.click('button:has-text("Add to my care")');
  await B.waitForSelector('textarea');
  await B.fill('textarea', 'Second opinion requested');
  await shot(B, '10-doctorB-add-to-care');
  await B.click('.ct-dialog-foot button:has-text("Add to my care")');
  ok('B adds the patient to their care', await waitText(B, 'On your list', 15000));
  await B.click('.page-breadcrumb a:has-text("My Patients")');
  ok("the patient is now on B's list", await waitText(B, 'Pat Patient' + SUFFIX, 15000));
  await B.click('button:has-text("Open record")');
  ok('B can open the record now', await waitText(B, 'Patient Overview', 30000));
  await B.evaluate(() => { window.location.hash = '#/CareTeam/Messages'; });

  // A is told, A shares a patient with B
  await A.evaluate(() => { window.location.hash = '#/CareTeam/Messages'; });
  ok('A was told that B joined the care team', await waitText(A, 'added themselves', 15000));
  await shot(A, '11-doctorA-messages');
  await A.evaluate(() => { window.location.hash = '#/CareTeam/MyPatients'; });
  await A.waitForTimeout(2500);
  const shareRow = A.locator('tr', { hasText: 'Sam Shared' + SUFFIX });
  await shareRow.locator('button:has-text("Share")').click();
  await A.waitForSelector('.ct-dialog select');
  await A.waitForFunction(() => document.querySelectorAll('.ct-dialog select option').length > 1, null, { timeout: 15000 }).catch(() => { });
  const shareOptions = await A.$$eval('.ct-dialog select option', os => os.map(o => ({ text: o.textContent.trim(), index: o.index })));
  const shareTo = shareOptions.find(o => o.text.includes('Ben Beta' + SUFFIX));
  ok("the other doctor is offered with the speciality", !!shareTo && shareTo.text.includes('Paediatrics'), shareOptions.map(o => o.text).join(' | '));
  await A.selectOption('.ct-dialog select', { index: shareTo.index });
  await A.fill('.ct-dialog textarea', 'Please take over the follow-up of this child.');
  await shot(A, '12-doctorA-share-dialog');
  await A.click('.ct-dialog-foot button:has-text("Share patient")');
  ok('A shares the patient with B', await waitText(A, 'has been shared', 15000));

  // A sends a message about a patient
  await A.evaluate(() => { window.location.hash = '#/CareTeam/Messages'; });
  await A.waitForTimeout(2000);
  await A.click('button:has-text("+ New message")');
  await A.waitForSelector('.ct-dialog input[type="checkbox"]');
  await A.locator('.ct-dialog label', { hasText: 'Ben Beta' + SUFFIX }).locator('input').check();
  await A.fill('.ct-dialog input[name="subject"]', 'Lab results are in');
  await A.fill('.ct-dialog textarea[name="body"]', 'Could you look at the results for Pat Patient?');
  await A.selectOption('.ct-dialog select[name="attach"]', { label: 'Pat Patient' + SUFFIX + ' (' + '' }).catch(async () => {
    const value = await A.$$eval('.ct-dialog select[name="attach"] option', os => { const o = os.find(x => x.textContent.indexOf('Pat Patient') >= 0); return o ? o.value : null; });
    if (value) await A.selectOption('.ct-dialog select[name="attach"]', value);
  });
  await A.click('.ct-dialog-foot button:has-text("Send")');
  await A.waitForTimeout(1500);

  // B's inbox
  await B.evaluate(() => { window.location.hash = '#/CareTeam/MyPatients'; });
  await B.waitForTimeout(500);
  await B.evaluate(() => { window.location.hash = '#/CareTeam/Messages'; });
  ok("B's inbox has the share notice and the message", await waitText(B, 'Patient shared with you', 20000) && (await bodyText(B)).includes('Lab results are in'));
  await shot(B, '13-doctorB-inbox');
  await B.click('.msg-row:has-text("Lab results are in")');
  ok('B opens the message and sees the patient attached', await waitText(B, 'On your list', 10000) && (await bodyText(B)).includes('Could you look at the results'));
  await shot(B, '14-doctorB-message');
  await B.click('button:has-text("Reply")');
  await B.waitForSelector('.ct-dialog textarea[name="body"]');
  await B.fill('.ct-dialog textarea[name="body"]', 'Thanks, I will look at them this afternoon.');
  await B.click('.ct-dialog-foot button:has-text("Send")');
  await B.waitForTimeout(1200);
  await A.evaluate(() => { window.location.hash = '#/CareTeam/MyPatients'; });
  await A.waitForTimeout(500);
  await A.evaluate(() => { window.location.hash = '#/CareTeam/Messages'; });
  ok("A receives B's reply", await waitText(A, 'Re: Lab results are in', 20000));

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nA doctor who has to choose a new password');
  const C = await newSession(browser, 'C');
  ok('doctor C signs in with the issued password', await signIn(C, credC.user, credC.password));
  ok('C is taken to the change-password page', /ChangePassword/.test(C.url()) || await waitText(C, 'Change Password', 8000), C.url());
  await shot(C, '15-doctorC-must-change-password');
  const pw = C.locator('input[type="password"]');
  await pw.nth(0).fill(credC.password);
  await pw.nth(1).fill('NewSecret' + SUFFIX.slice(0, 4));
  await pw.nth(2).fill('NewSecret' + SUFFIX.slice(0, 4));
  await C.click('button:has-text("Change Password")');
  ok('C changes the password', await waitText(C, 'Password Updated Successfuly', 15000) || /UserProfile/.test(C.url()), C.url());
  const C2 = await newSession(browser, 'C2');
  ok('C signs in with the new password and lands on My Patients', await signIn(C2, credC.user, 'NewSecret' + SUFFIX.slice(0, 4)) && await waitText(C2, 'My Patients', 15000), C2.url());
  const C3 = await newSession(browser, 'C3');
  expectFailures++;
  await signIn(C3, credC.user, credC.password);
  ok('the old password no longer works', /username or password is not right/i.test(await bodyText(C3)));
  expectFailures--;

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nAdministrator withdraws doctor B');
  await admin.evaluate(() => { window.location.hash = '#/DoctorAdmin'; });
  await admin.waitForTimeout(500);
  await admin.reload({ waitUntil: 'load' });
  await admin.waitForTimeout(6000);
  await waitText(admin, docB.user, 20000);
  await shot(admin, '15b-admin-before-withdraw');
  const rowB = admin.locator('tr', { hasText: docB.user });
  await rowB.locator('button:has-text("Withdraw")').click();
  await admin.waitForSelector('.ct-dialog select');
  await admin.fill('.ct-dialog input[name="reason"]', 'left the hospital');
  const handOverOptions = await admin.$$eval('.ct-dialog select[name="handover"] option', os => os.map(o => o.textContent.trim()));
  ok('the admin can hand the patients over to another doctor', handOverOptions.some(o => o.includes('Anna Alpha' + SUFFIX)), handOverOptions.join(' | '));
  await admin.selectOption('.ct-dialog select[name="handover"]', { label: handOverOptions.find(o => o.includes('Anna Alpha' + SUFFIX)) });
  await shot(admin, '16-admin-withdraw-dialog');
  await admin.click('.ct-dialog-foot button:has-text("Withdraw")');
  await admin.waitForTimeout(1500);
  await admin.check('input[type="checkbox"]').catch(() => { });          // "Show withdrawn"
  ok('B shows as withdrawn', await waitText(admin, 'Withdrawn', 10000));
  await shot(admin, '17-admin-withdrawn');

  expectFailures++;
  await B.evaluate(() => { window.location.hash = '#/CareTeam/MyPatients'; });
  await B.waitForTimeout(4000);
  const bAfter = await bodyText(B);
  ok("B's open session stops working (sent back to the sign-in page)", /Sign in/i.test(bAfter) && /Forgot/i.test(bAfter) || /Unauthorized/i.test(bAfter), B.url() + ' ' + bAfter.slice(0, 120));
  expectFailures--;
  const B2 = await newSession(browser, 'B2');
  expectFailures++;
  await signIn(B2, credB.user, credB.password);
  const b2Text = await bodyText(B2);
  ok('B cannot sign in again and is told why', /switched off|withdrawn/i.test(b2Text), b2Text.slice(0, 200));
  await shot(B2, '18-withdrawn-doctor-sign-in');
  expectFailures--;

  // ------------------------------------------------------------------------------------------------------------------
  console.log('\nThe sign-in page');
  const X = await newSession(browser, 'X');
  expectFailures++;
  await X.goto(BASE + '/', { waitUntil: 'load' });
  await X.fill('#username_id', 'nobody');
  await X.fill('#password', 'wrong');
  await Promise.all([X.waitForNavigation({ waitUntil: 'load' }).catch(() => null), X.click('#login')]);
  const bad = await bodyText(X);
  ok('a wrong password gives a clear message', /username or password is not right/i.test(bad), bad.slice(0, 200));
  await shot(X, '19-wrong-password');
  expectFailures--;

  await cleanUp(admin, createdUsers);
  await browser.close();

  console.log('\nUnexpected "Failed" answers or errors seen during the story: ' + unexpected.length);
  unexpected.slice(0, 25).forEach(u => console.log('   ! ' + u));
  ok('no unexpected refusals or errors while doctors used their screens', unexpected.length === 0);
  console.log('\n' + passed + ' checks passed, ' + failed.length + ' failed');
  failed.forEach(f => console.log('  FAILED: ' + f));
  process.exit(failed.length ? 1 : 0);
})().catch(async e => {
  console.error('TEST CRASHED:', e);
  if (adminPageForCleanUp) await cleanUp(adminPageForCleanUp, createdUsers);
  process.exit(2);
});
