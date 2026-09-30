# Danphe EMR – start here

This is the complete Danphe hospital system, set up so that **doctors each have their own login, keep their own patients, and can
share patients and message each other** – the way a doctor would use it every day. The administrator issues the logins.

* **Sign in as the administrator:** username `admin` · password `123` (the username is not case sensitive – `Admin` works too).
  Change the password after your first sign-in: top right → **admin** → *My Profile* → *Change Password* (6–20 characters).
* The software licence is set to run until 2099 – there is no licence screen.
* It is **not** the demo version: patients, logins and messages are saved in a real database and are still there after a restart.

---

## 1. How do I open it? (pick one)

### A) On my Mac (or Windows / Linux computer) – double-click

1. Install **Docker Desktop** (free): <https://www.docker.com/products/docker-desktop/> – open it once and wait until it says *running*.
   * Apple-silicon Macs (M1/M2/M3/M4): in Docker Desktop → *Settings → General* tick **"Use Rosetta for x86_64/amd64 emulation"**
     (it is on by default in recent versions).
   * Docker Desktop → *Settings → Resources*: give it at least **6 GB of memory** for the first start (building the screens needs it;
     afterwards about 3 GB are used).
2. Download this repository (green **Code** button → *Download ZIP*) and unzip it – or `git clone` it.
3. Double-click **`Start Danphe EMR.command`**.
   * The first time, macOS may say it "cannot be opened because it is from an unidentified developer": **right-click the file → Open → Open**.
     If it says you have no permission, open *Terminal*, type `chmod +x ` (with a space), drag the file into the window, press Return, and try again.
   * The first start builds the program and creates the database: **15–40 minutes, once**. Later starts take about a minute.
4. Your browser opens at **<http://localhost:8080>** – that is the link. Sign in as `admin` / `123`.

To stop it: double-click **`Stop Danphe EMR.command`**. Nothing is lost – patients, logins and uploaded scans are back next time.
(Terminal users: `./docker/start.sh` and `./docker/stop.sh`, or `docker compose up -d --build` / `docker compose stop`.)

### B) In the browser only – GitHub Codespaces (no install, works on any Mac)

Open <https://codespaces.new/ginovokes-dev/hospital-management-emr/tree/claude/brave-thompson-whs0cb?quickstart=1>
(needs a GitHub account with access to the repository and Codespaces). Make sure the **branch** shown is `claude/brave-thompson-whs0cb`
(on GitHub: *Code → Codespaces → Create codespace on claude/brave-thompson-whs0cb*).
It creates a cloud computer (4 cores, 16 GB), builds and starts the system by itself (20–40 minutes the first time) and shows a short
"how to open it" note. To watch the progress press **Cmd+Shift+P**, type **Creation Log** and press Enter; when it says **"Danphe EMR is ready"**
the system opens in a browser tab – if it does not, open the **Ports** tab and click the globe icon next to **8080**.
If the codespace says it is in "recovery mode", something went wrong while building it: open the Creation Log, and run `git pull` followed by
**Cmd+Shift+P → Codespaces: Rebuild Container**.
That link is private to you. A Codespace is good for trying the system out; it goes to sleep when idle and is not a place to keep real
patient records – for real use, run it on a computer or server of the hospital (option A, on a server – see section 5).

---

## 2. What the administrator does (once)

Sign in as `admin` → you land on **Manage Doctors**.

* **+ Add doctor** – name, department, speciality, role (*Doctor*), username and password (the *Generate* button makes one). The screen then
  shows the username and password once: give them to the doctor. By default the doctor is asked to choose their own password at the first sign-in.
* **Edit** – change name, speciality, role. **Reset password** – if a doctor forgets theirs.
* **Withdraw** – switches the login off at once (even if the doctor is signed in) and can hand the doctor's patients over to a colleague.
  Nothing is deleted; **Re-activate** brings the doctor back.
* **Activity log** – who searched for, added, shared or was refused a patient.

Only the administrator can do all of this – the server refuses everybody else, even if they try the addresses by hand.

## 3. What a doctor sees and does

A doctor signs in with the username and password the administrator gave them and lands on **Care Team → My Patients**.

| I want to … | Do this |
|---|---|
| see **my** patients | *My Patients* – only the patients under my care are listed |
| add a new patient | *My Patients → + Register new patient* (name, sex, date of birth …) – the patient is on my list |
| work on a patient | *Open record* (or *New consultation* for today). In the record: overview, problems, current medications, encounter history, **orders** (lab tests are signed from here), clinical documents, clinical (vitals, allergies, medication, progress notes), notes, **scanned images** (upload a scan or photo) and *Conclude visit* |
| find a patient who is under **another** doctor | *Find Patient* → type the name → the result shows **which doctor(s) the patient is under** (name, number, sex and age only) → *Add to my care* |
| hand a patient to a colleague | *Share* on the patient → choose the doctor → they get the patient on their list and a message |
| write to a colleague ("e-mail" inside the system) | *Messages → + New message* – optionally about one of my patients |
| leave a patient | *Remove* (the patient and record are kept; the other doctors are told) |

Each doctor **only sees their own patients**. Another doctor cannot open a record until they add themselves to the care team – and that is
recorded in the activity log and announced to the doctors already looking after the patient. Doctors only get the clinical screens
(no billing, accounting, settings or user administration). The *OPD Summary* tab of the original product is not offered: it needs a form
the hospital has to design first, and the sample database has none.

## 4. Good to know

* **Where is my data?** In two Docker "volumes": `danphe-emr_dbdata` (the database) and `danphe-emr_appfiles` (uploaded scans and
  documents). They survive restarts and updates. Back both up regularly – see `docker/README.md` for the commands.
  `docker compose down -v` **erases everything**.
* **Start with sample data instead of clean?** Set `FRESH_START=0` in `.env` before the very first start (sample staff logins `Pooja`/`Amit` …
  are then kept). By default the sample staff logins are switched off and the eleven made-up sample patients are hidden.
* **Wrong passwords:** after 8 wrong attempts for one user name the sign-in pauses for 5 minutes (it protects against password guessing).
  The administrator can always reset a doctor's password.
* **Reloading the page (F5)** keeps you signed in and brings you back to the same screen. Inside an open patient record the chosen
  patient is forgotten (the system says "Please select a patient-visit first"): click *Care Team → My Patients* and open the patient again.
* The system only listens on this computer (`127.0.0.1`). To let other computers in the hospital use it, set `BIND_ADDRESS=0.0.0.0` in `.env`
  and restart – and put it behind HTTPS and the hospital's firewall (see section 5) before real patient data goes in.

## 5. Before real patients (please read)

This work was built and tested automatically (see the next section), but **it has not had an independent security or clinical-safety review**,
and it has been run and tested on Linux with Docker – **not yet on a real Mac**.
Before real patient data is stored:

1. Change the `admin` password, and only share the address with people who should have it. Use HTTPS (put a reverse proxy such as Caddy or nginx
   with a certificate in front of port 8080) if it is reachable by other computers.
2. Have your IT person read `Code/Websites/DanpheEMR/CareTeam/` (the access rules) and check backups, updates and data-protection law/regulation for your country.
3. Known limits: sign-in tokens are valid for 24 hours and everybody is signed out when the program restarts; the original product's
   *DICOM listener* endpoint is unchanged; the older Windows-era modules (billing, pharmacy …) are as they were – only doctors are confined;
   the program runs on Mono (the open-source .NET runtime) instead of Windows, which works but is not a combination Microsoft supports;
   passwords are stored the way the original product stores them (reversible encryption with a key that is part of the program, not a one-way hash) –
   anybody who can read the database can recover them;
   the database is the free *SQL Server Express* (10 GB per database – enough for a long time, but a hospital that grows beyond it needs a licensed edition);
   the doctor rules recognise requests by patient, visit and clinical-record numbers – a technically skilled doctor who hand-crafts requests
   for rarely used screens that address data by some other number might still reach text of a colleague's patient (without the patient's identity).

## 6. Tests

* `python3 tests/api_tests.py` – ~140 checks of the login / care-team / administrator / privacy rules against a running system (`BASE_URL=http://localhost:8080`).
* `node tests/e2e/browser_flow.js` – a real-browser walk-through: admin adds doctors, doctors register, open records, record vitals, upload a scan,
  sign a lab order, find, share, message, choose a new password, get withdrawn (needs `npm i playwright`).
Both create their own test doctors and withdraw them again at the end.

## 7. What was changed compared with the original repository

Original Danphe EMR code is kept; additions are in their own folders:

| Where | What |
|---|---|
| `Code/Websites/DanpheEMR/CareTeam/` | server: access rules for every request, care teams, messages, Manage Doctors, sign-in lockout, database upgrade at start-up |
| `Code/Websites/DanpheEMR/wwwroot/DanpheApp/src/app/care-team`, `doctor-admin` | screens: My Patients, Find Patient, Messages, Manage Doctors |
| `Database/3. Doctor-Care-Team/` | SQL: care-team tables, menu entries, admin login + licence, "not a demo", attachment tables and upload folders for Linux/Mac |
| `docker/`, `docker-compose.yml`, `Start/Stop Danphe EMR.command`, `.devcontainer/` | run it on a Mac / Linux / server / Codespaces |
| small edits to existing files | sign-in page wording; "Home" in a patient record returns to My Patients; the page no longer blanks on reload; 7 letter-case fixes so it builds on Linux/Mac |
