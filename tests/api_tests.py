#!/usr/bin/env python3
"""
End-to-end checks of the doctor / care-team / administrator rules against a RUNNING server (no browser needed, Python 3 standard
library only).

    BASE_URL=http://127.0.0.1:5001 python3 tests/api_tests.py

It creates two test doctors ("Test Doctor A/B" with a random suffix), test patients and messages, then withdraws the doctors again.
Run it against a test installation - it writes data (it never deletes patients; doctors are only withdrawn).
"""
import base64
import hashlib
import hmac
import http.cookiejar
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = os.environ.get("BASE_URL", "http://127.0.0.1:5001").rstrip("/")
ADMIN_USER = os.environ.get("ADMIN_USER", "admin")
ADMIN_PASSWORD = os.environ.get("ADMIN_PASSWORD", "123")
SUFFIX = uuid.uuid4().hex[:6]

passed = 0
failed = []


class Client:
    def __init__(self, name):
        self.name = name
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.token = None

    def login(self, user, password):
        body = json.dumps({"UserName": user, "Password": password}).encode()
        req = urllib.request.Request(BASE + "/api/Account/GetLoginJwtToken", body, {"Content-Type": "application/json"})
        try:
            with self.opener.open(req, timeout=60) as r:
                self.token = json.loads(r.read().decode())["loginJwtToken"]
                return True
        except urllib.error.HTTPError:
            self.token = None
            return False

    def call(self, method, path, body=None, token="__own__", raw=False):
        headers = {}
        tok = self.token if token == "__own__" else token
        if tok:
            headers["Authorization"] = "Bearer " + tok
        data = None
        if body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(BASE + path, data, headers, method=method)
        try:
            with self.opener.open(req, timeout=120) as r:
                text = r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8", "replace")
        if raw:
            return text
        try:
            value = json.loads(text)
            if isinstance(value, str):          # a few calls answer with a JSON string that itself holds the JSON
                value = json.loads(value)
            return value
        except ValueError:
            return {"Status": "Failed", "ErrorMessage": "not json: " + text[:200], "Results": None}

    def upload(self, path, fields, files):
        """multipart/form-data POST: fields = {name: text}, files = [(field, file name, bytes, content type)]"""
        boundary = "----danphe" + uuid.uuid4().hex
        parts = []
        for name, value in fields.items():
            parts.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n%s\r\n" % (boundary, name, value)).encode())
        for field, filename, data, ctype in files:
            head = "--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\nContent-Type: %s\r\n\r\n" % (boundary, field, filename, ctype)
            parts.append(head.encode() + data + b"\r\n")
        parts.append(("--%s--\r\n" % boundary).encode())
        headers = {"Content-Type": "multipart/form-data; boundary=" + boundary}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(BASE + path, b"".join(parts), headers, method="POST")
        try:
            with self.opener.open(req, timeout=120) as r:
                text = r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8", "replace")
        try:
            value = json.loads(text)
            return json.loads(value) if isinstance(value, str) else value
        except ValueError:
            return {"Status": "Failed", "ErrorMessage": "not json: " + text[:200], "Results": None}

    def download(self, path):
        """the raw bytes of a file download, or None when the server answered with a refusal (JSON) instead"""
        headers = {"Authorization": "Bearer " + self.token} if self.token else {}
        req = urllib.request.Request(BASE + path, None, headers, method="GET")
        try:
            with self.opener.open(req, timeout=120) as r:
                data = r.read()
        except urllib.error.HTTPError:
            return None
        return None if data[:1] in (b"{", b'"') and b"Status" in data[:80] else data

    def get(self, path, **kw):
        return self.call("GET", path, **kw)

    def post(self, path, body=None, **kw):
        return self.call("POST", path, body if body is not None else {}, **kw)


def check(name, condition, detail=""):
    global passed
    if condition:
        passed += 1
        print("  ok    " + name)
    else:
        failed.append(name)
        print("  FAIL  " + name + ("   -> " + str(detail)[:300] if detail else ""))


def ok(r):
    return r.get("Status") == "OK"


def msg(r):
    return r.get("ErrorMessage") or ""


def denied(r):
    return r.get("Status") == "Failed"


def section(title):
    print("\n" + title)


# ---------------------------------------------------------------------------------------------------------------------
admin = Client("admin")
section("Signing in")
check("wrong password is refused", not Client("x").login(ADMIN_USER, "definitely-wrong"))
check("admin can sign in", admin.login(ADMIN_USER, ADMIN_PASSWORD))
check("the user name is not case sensitive (ADMIN)", Client("x").login(ADMIN_USER.upper(), ADMIN_PASSWORD))
if not admin.token:
    print("cannot continue without the admin login")
    sys.exit(2)

section("Password guessing is slowed down")
ghost = "nobody" + SUFFIX
codes = []
for i in range(9):
    req = urllib.request.Request(BASE + "/api/Account/GetLoginJwtToken", json.dumps({"UserName": ghost, "Password": "guess%d" % i}).encode(), {"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=60)
        codes.append(200)
    except urllib.error.HTTPError as e:
        codes.append(e.code)
check("the first wrong attempts get a plain refusal (401)", codes[:8] == [401] * 8, codes)
check("after 8 wrong attempts sign-in is paused for that user name (429)", codes[8] == 429, codes)
check("other users can still sign in", Client("x").login(ADMIN_USER, ADMIN_PASSWORD))
# the change-password call also checks a password, so it counts towards the same limit (it must not be a way around it)
ghost2 = "nobody2" + SUFFIX
answers = [Client("x").call("PUT", "/Account/ChangePassword", {"UserName": ghost2, "Password": "guess%d" % i, "NewPassword": "abcdef1", "ConfirmPassword": "abcdef1"}, token=None) for i in range(9)]
check("change-password: wrong current passwords are refused", all(denied(a) for a in answers[:8]), answers[:8])
check("change-password: after 8 wrong attempts it is paused as well", denied(answers[8]) and "Too many" in msg(answers[8]), answers[8])

section("Nothing works without signing in")
anon = Client("anon")
for path in ["/api/DoctorAdmin/Staff", "/api/CareTeam/MyPatients", "/api/Doctors/TodaysVisits", "/api/Patient/MatchingPatients", "/Reporting/HomeDashboardStats",
             "/api/Messages/Inbox"]:
    r = anon.get(path)
    check("anonymous: " + path, denied(r) and "Unauthorized" in msg(r), r)
r = anon.post("/DynamicReporting/GetReportData", {"Query": "select top 1 * from PAT_Patient"})
check("anonymous cannot run the report query tool", denied(r), r)


def b64(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


sample_key = b"Danphe_EMR@1234567890#" * 5
head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
claims = b64(json.dumps({"currentUser": json.dumps({"UserId": 1, "EmployeeId": 1, "UserName": "admin"}), "exp": int(time.time()) + 3600}).encode())
forged = head + "." + claims + "." + b64(hmac.new(sample_key, (head + "." + claims).encode(), hashlib.sha256).digest())
r = admin.get("/api/DoctorAdmin/Staff", token=forged)
check("a token signed with the public sample key is refused", denied(r), r)
unsigned = b64(json.dumps({"alg": "none", "typ": "JWT"}).encode()) + "." + claims + "."
check("an unsigned token is refused", denied(admin.get("/api/DoctorAdmin/Staff", token=unsigned)))
check("a token belonging to someone else's session is refused", denied(Client("x").get("/api/DoctorAdmin/Staff", token=admin.token)))

# ---------------------------------------------------------------------------------------------------------------------
section("Administrator: issue doctor logins")
lk = admin.get("/api/DoctorAdmin/Lookups")
check("lookups load (roles, departments)", ok(lk) and lk["Results"]["Roles"] and lk["Results"]["Departments"], lk)
doctor_role = next((x["RoleId"] for x in lk["Results"]["Roles"] if x["IsDoctorRole"]), None)
depts = lk["Results"]["Departments"]
check("a doctor role exists", doctor_role is not None)

userA, userB = "dr.alpha" + SUFFIX, "dr.beta" + SUFFIX
pwA, pwB = "alphaPass" + SUFFIX, "betaPass" + SUFFIX


def new_doctor(first, last, user, pw, dept, speciality):
    return {"Salutation": "Dr", "FirstName": first, "LastName": last, "Gender": "Female", "Email": user + "@example.org", "ContactNumber": "0700000000",
            "DepartmentId": dept["DepartmentId"], "Speciality": speciality, "RoleId": doctor_role, "UserName": user, "Password": pw, "MustChangePassword": False}


r = admin.post("/api/DoctorAdmin/Staff", new_doctor("Test", "Alpha" + SUFFIX, userA, pwA, depts[0], "Cardiology"))
check("admin creates doctor A", ok(r), r)
idA = r["Results"]["EmployeeId"] if ok(r) else 0
r = admin.post("/api/DoctorAdmin/Staff", new_doctor("Test", "Beta" + SUFFIX, userB, pwB, depts[1], "Paediatrics"))
check("admin creates doctor B", ok(r), r)
idB = r["Results"]["EmployeeId"] if ok(r) else 0
uidA = uidB = 0
staff = admin.get("/api/DoctorAdmin/Staff")["Results"]
for s in staff:
    if s["UserName"] == userA: uidA = s["UserId"]
    if s["UserName"] == userB: uidB = s["UserId"]
check("both appear in the staff list with their speciality", uidA and uidB and any(s["UserName"] == userA and s["Speciality"] == "Cardiology" for s in staff))
check("a duplicate user name is refused (any capitalisation)", denied(admin.post("/api/DoctorAdmin/Staff", new_doctor("X", "Y", userA.upper(), "whatever1", depts[0], None))))
check("a too-short password is refused", denied(admin.post("/api/DoctorAdmin/Staff", new_doctor("X", "Y", "dr.short" + SUFFIX, "123", depts[0], None))))
check("a password longer than 20 characters is refused (the change-password form cannot take it)", denied(admin.post("/api/DoctorAdmin/Staff", new_doctor("X", "Y", "dr.long" + SUFFIX, "x" * 21, depts[0], None))))
check("a doctor needs a department", denied(admin.post("/api/DoctorAdmin/Staff", dict(new_doctor("X", "Y", "dr.nodept" + SUFFIX, "longenough1", depts[0], None), DepartmentId=None))))

# ---------------------------------------------------------------------------------------------------------------------
section("Doctors sign in with the logins they were given")
A, B = Client("A"), Client("B")
check("doctor A signs in", A.login(userA, pwA))
check("doctor B signs in (user name in capitals)", B.login(userB.upper(), pwB))
check("doctor A's old/other password does not work", not Client("x").login(userA, pwB))

section("A doctor who has to choose a new password at the first sign-in")
userD, pwD, pwD2 = "dr.delta" + SUFFIX, "deltaPass" + SUFFIX, "myOwnSecret" + SUFFIX[:3]
r = admin.post("/api/DoctorAdmin/Staff", dict(new_doctor("Test", "Delta" + SUFFIX, userD, pwD, depts[0], "Neurology"), MustChangePassword=True))
check("admin creates doctor D with 'must change password'", ok(r), r)
uidD = next((x["UserId"] for x in admin.get("/api/DoctorAdmin/Staff")["Results"] if x["UserName"] == userD), 0)
D = Client("D")
check("doctor D signs in with the issued password", D.login(userD, pwD))
r = D.call("PUT", "/Account/ChangePassword", {"UserName": userD, "Password": "not-my-password", "NewPassword": pwD2, "ConfirmPassword": pwD2})
check("a wrong current password is refused", denied(r), r)
check("... and changes nothing", Client("x").login(userD, pwD))
r = D.call("PUT", "/Account/ChangePassword", {"UserName": userD, "Password": pwD, "NewPassword": pwD2, "ConfirmPassword": pwD2})
check("D chooses a new password", ok(r), r)
check("the new password works", Client("x").login(userD, pwD2))
check("the issued password no longer works", not Client("x").login(userD, pwD))
check("D is no longer asked to change it (next sign-in)", next((x["MustChangePassword"] for x in admin.get("/api/DoctorAdmin/Staff")["Results"] if x["UserName"] == userD), None) in (False, None))

section("Doctors cannot do the administrator's work")
for label, res in [("open Manage Doctors data", A.get("/api/DoctorAdmin/Staff")),
                   ("create a doctor", A.post("/api/DoctorAdmin/Staff", new_doctor("Sneaky", "Doc", "sneaky" + SUFFIX, "sneaky-pass", depts[0], None))),
                   ("reset the admin's password", A.call("PUT", "/api/SecuritySettings/ResetPassword", {"UserId": 1, "Password": "hacked"})),
                   ("list logins", A.get("/api/SecuritySettings/Users")),
                   ("change a role", A.call("PUT", "/api/SecuritySettings/UserRoles", [])),
                   ("edit an employee", A.call("PUT", "/api/EmployeeSettings/Employees", {"EmployeeId": idB}))]:
    check("doctor cannot " + label, denied(res) and "administrator" in msg(res).lower(), res)
check("the admin password still works", Client("x").login(ADMIN_USER, ADMIN_PASSWORD))

section("Other staff (not doctors) keep the screens of their role")
billing_role = next((x["RoleId"] for x in lk["Results"]["Roles"] if x["RoleName"] == "Billing"), None)
userF, pwF = "clerk.fox" + SUFFIX, "foxPass" + SUFFIX
r = admin.post("/api/DoctorAdmin/Staff", dict(new_doctor("Fay", "Fox" + SUFFIX, userF, pwF, depts[0], None), RoleId=billing_role, Salutation="Ms"))
check("admin creates a login for a billing clerk", billing_role and ok(r), r)
uidF = next((x["UserId"] for x in admin.get("/api/DoctorAdmin/Staff")["Results"] if x["UserName"] == userF), 0)
F = Client("F")
check("the clerk signs in", F.login(userF, pwF))
check("the clerk can use the billing screens", ok(F.get("/api/Billing/BillingCounters")), F.get("/api/Billing/BillingCounters"))
r = F.get("/api/DoctorAdmin/Staff")
check("... but cannot manage doctors and logins", denied(r) and "administrator" in msg(r).lower(), r)
r = F.get("/api/SecuritySettings/Users")
check("... or the user administration", denied(r) and "administrator" in msg(r).lower(), r)

section("Doctors are limited to the clinical screens")
for path in ["/api/Patient/MatchingPatients", "/api/Appointment/Appointments", "/api/Billing/SsfInvoices", "/api/SystemAdmin/DatabaseBakupLogs"]:
    r = A.get(path)
    check("doctor blocked from " + path, denied(r) and "not available to doctor" in msg(r), r)
r = A.post("/DynamicReporting/GetReportData", {"Query": "select * from PAT_Patient"})
check("doctor cannot use the report query tool", denied(r) and "not available to doctor" in msg(r), r)
r = A.get("/BillingReports/BillDetailReport?FromDate=2023-01-01&ToDate=2026-12-31")
check("doctor cannot open billing reports", denied(r) and "not available to doctor" in msg(r), r)
check("the doctor screens' lookups still load", ok(A.get("/api/Master/Countries")) and ok(A.get("/api/Security/UserPermissions")))

# ---------------------------------------------------------------------------------------------------------------------
section("Doctors and their own profile (the change-password page needs it)")
r = A.get("/api/Employee/Profile?empId=%d" % idA)
check("a doctor can open their own profile", ok(r), r)
r = A.get("/api/Employee/Profile?empId=%d" % idB)
check("... but not a colleague's", denied(r) and "your own profile" in msg(r), r)
r = A.post("/api/Employee/LandingPage", {"UserId": 1, "LandingPageRouteId": 1})
check("a doctor cannot change the administrator's landing page", denied(r) and "your own profile" in msg(r), r)
r = A.post("/api/Employee/LandingPage", {"UserId": uidA, "LandingPageRouteId": None})
check("... but can change their own", ok(r), r)

section("Patients: each doctor sees their own")
r = A.get("/api/CareTeam/MyPatients")
check("A starts with no patients", ok(r) and r["Results"] == [], r)
reg = {"Salutation": "Mr", "FirstName": "Pat", "LastName": "Alpha" + SUFFIX, "Gender": "Male", "DateOfBirth": "1980-05-17", "PhoneNumber": "0711111111"}
r = A.post("/api/CareTeam/RegisterPatient", reg)
check("doctor A registers a patient", ok(r) and r["Results"]["PatientId"], r)
p1 = r["Results"]["PatientId"] if ok(r) else 0
check("the patient gets a patient number", ok(r) and r["Results"]["PatientCode"], r)
check("registration needs a date of birth", denied(A.post("/api/CareTeam/RegisterPatient", dict(reg, DateOfBirth=None))))

mine = A.get("/api/CareTeam/MyPatients")["Results"]
check("A sees the patient in My Patients", any(x["PatientId"] == p1 for x in mine), mine)
check("B does not see it in My Patients", not any(x["PatientId"] == p1 for x in B.get("/api/CareTeam/MyPatients")["Results"]))

r = B.get("/api/CareTeam/FindPatient?search=" + urllib.parse.quote("Alpha" + SUFFIX))
found = r["Results"] if ok(r) else []
check("B can find the patient by name and sees who they are under", any(x["PatientId"] == p1 and any(t["EmployeeId"] == idA for t in x["Team"]) and not x["IsMine"] for x in found), r)
check("the search result shows no phone / address", found and "PhoneNumber" not in found[0] and "Address" not in found[0])
check("a one-letter search is refused", denied(B.get("/api/CareTeam/FindPatient?search=a")))

for label, path in [("allergies", "/api/Clinical/PatientAllergies?patientId=%d" % p1), ("overview", "/api/Doctors/PatientOverview?patientId=%d&patientVisitId=0" % p1),
                    ("vitals", "/api/Clinical/LatestVitals?patientId=%d" % p1), ("visit history", "/api/Visit/PatientVisitHistory?patientId=%d" % p1),
                    ("team", "/api/CareTeam/Team?patientId=%d" % p1)]:
    r = B.get(path)
    check("B cannot open A's patient (%s)" % label, denied(r) and "not under your care" in msg(r), r)
r = B.post("/api/Clinical/Allergy", {"PatientId": p1, "AllergenAdvRecName": "x"})
check("B cannot write to A's patient", denied(r) and "not under your care" in msg(r), r)
check("A can read their own patient's allergies", ok(A.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)), A.get("/api/Clinical/PatientAllergies?patientId=%d" % p1))
check("A can read the team", ok(A.get("/api/CareTeam/Team?patientId=%d" % p1)))

section("Starting a consultation creates a visit only A can reach")
r = A.post("/api/CareTeam/OpenChart", {"PatientId": p1, "NewConsultation": True})
check("A opens the chart (a visit is created)", ok(r) and r["Results"]["Visit"]["PatientVisitId"], r)
visit1 = r["Results"]["Visit"]["PatientVisitId"] if ok(r) else 0
r2 = A.post("/api/CareTeam/OpenChart", {"PatientId": p1})
check("opening again reuses today's visit", ok(r2) and r2["Results"]["Visit"]["PatientVisitId"] == visit1, r2)
check("A's visit shows on A's list", any(v["PatientId"] == p1 for g in A.get("/api/Doctors/TodaysVisits?toDate=%s" % time.strftime("%Y-%m-%d"))["Results"] for v in g["visit"]))
groups = B.get("/api/Doctors/TodaysVisits?toDate=%s" % time.strftime("%Y-%m-%d"))
check("B's list of today's visits does not contain A's patient", ok(groups) and not any(v["PatientId"] == p1 for g in groups["Results"] for v in g["visit"]), groups)
check("B cannot use A's visit number either", denied(B.get("/api/Clinical/LatestVitals?patientId=0&patientVisitId=%d" % visit1)))
check("B cannot start a consultation for A's patient", denied(B.post("/api/CareTeam/OpenChart", {"PatientId": p1, "NewConsultation": True})))

section("Documents, scanned images and record numbers stay with the patient's own doctors")
pdf = b"%PDF-1.4 test document " + SUFFIX.encode()
r = A.upload("/api/Patient/PatientFiles", {"reportDetails": json.dumps({"PatientId": p1, "FileType": "Referral", "Title": "x"})}, [("uploads", "x.pdf", pdf, "application/pdf")])
check("doctors do not upload documents through the Patient screens (reception does)", denied(r) and "not available to doctor" in msg(r), r)
r = admin.upload("/api/Patient/PatientFiles", {"reportDetails": json.dumps({"PatientId": p1, "FileType": "Referral", "Title": "Letter " + SUFFIX, "Description": "test"})},
                 [("uploads", "Referral_letter.pdf", pdf, "application/pdf")])
check("the administrator uploads a document for A's patient", ok(r), r)
docs = A.get("/api/Patient/PatientDocuments?patientId=%d" % p1)
docs_list = docs["Results"] if ok(docs) and isinstance(docs["Results"], list) else []
check("... and it is listed", len(docs_list) == 1 and docs_list[0]["FileType"] == "Referral", docs)
fileId = docs_list[0]["PatientFileId"] if docs_list else 0
check("A can download it again, byte for byte", fileId and A.download("/api/Patient/DownloadFile?patientFileId=%d" % fileId) == pdf)
r = A.get("/api/Patient/LightPatientById?patientId=%d" % p1)
check("A can open the patient's basic details", ok(r) and r["Results"]["PatientId"] == p1, r)

r = B.get("/api/Patient/LightPatientById?patientId=%d" % p1)
check("B cannot open the patient's basic details", denied(r) and "not under your care" in msg(r), r)
r = B.get("/api/Patient/PatientDocuments?patientId=%d" % p1)
check("B cannot list the patient's documents", denied(r) and "not under your care" in msg(r), r)
check("B cannot download the document by its number", fileId and B.download("/api/Patient/DownloadFile?patientFileId=%d" % fileId) is None)
r = A.upload("/api/Clinical/PatientFiles", {"imgDetails": json.dumps({"PatientId": p1, "PatientVisitId": visit1, "FileType": "Other", "FileName": "x", "Title": "x"})},
             [("uploads", "../../evil.png", b"x", "image/png")])
check("a file name with a folder path is refused", denied(r) and "name" in msg(r), r)
check("other Patient calls stay closed to doctors", denied(A.get("/api/Patient/MatchingPatients?FirstName=a&LastName=b&Age=1&Gender=Male")))
r = B.post("/api/Billing/provisional-billing", {"PatientId": p1, "PatientVisitId": visit1, "BillingTransactionItems": []})
check("B cannot order (provisional bill) for A's patient", denied(r) and "not under your care" in msg(r), r)
check("the rest of Billing stays closed to doctors", denied(A.post("/api/Billing/PayProvisionalBills", {"PatientId": p1})))

png = b"\x89PNG\r\n\x1a\n" + b"scan" * 16
scan = {"PatientId": p1, "PatientVisitId": visit1, "DepartmentId": 1, "FileType": "Other", "FileName": "scan", "Title": "Chest " + SUFFIX, "Comment": "test"}
r = A.upload("/api/Clinical/PatientFiles", {"imgDetails": json.dumps(scan)}, [("uploads", "Other_scan.png", png, "image/png")])
check("A uploads a scanned image for their own patient", ok(r), r)
imgs = A.get("/api/Clinical/ScannedImages?patientId=%d" % p1)
imgs_list = imgs["Results"] if ok(imgs) and isinstance(imgs["Results"], list) else []
check("... and it is listed", len(imgs_list) == 1, imgs)
imgId = imgs_list[0]["PatImageId"] if imgs_list else 0
r = B.upload("/api/Clinical/PatientFiles", {"imgDetails": json.dumps(scan)}, [("uploads", "Other_scan.png", png, "image/png")])
check("B cannot upload a scan into A's patient", denied(r) and "not under your care" in msg(r), r)
check("B cannot list A's patient's scans", denied(B.get("/api/Clinical/ScannedImages?patientId=%d" % p1)))
r = B.call("PUT", "/api/Clinical/DeactivatePatientImage?patImageId=%d" % imgId)
check("B cannot remove A's patient's scan by its number", imgId and denied(r) and "not under your care" in msg(r), r)
check("the scan is still there", len(A.get("/api/Clinical/ScannedImages?patientId=%d" % p1)["Results"]) == 1)

r = B.post("/api/CareTeam/RegisterPatient", {"Salutation": "Ms", "FirstName": "Bea", "LastName": "Beta" + SUFFIX, "Gender": "Female", "DateOfBirth": "1990-01-02", "PhoneNumber": "0722222222"})
pB = r["Results"]["PatientId"] if ok(r) else 0
check("B registers a patient of their own", pB)
r = A.post("/api/Clinical/Allergy", {"PatientId": p1, "AllergenAdvRecName": "Penicillin " + SUFFIX, "Severity": "Severe", "Verified": True})
check("A records an allergy for their patient", ok(r), r)
allergies = A.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)
allergyId = next((x["PatientAllergyId"] for x in allergies["Results"] if "Penicillin " + SUFFIX in (x.get("AllergenAdvRecName") or "")), 0) if ok(allergies) else 0
check("... and finds it again", allergyId, allergies)
r = B.call("PUT", "/api/Clinical/Allergy", {"PatientAllergyId": allergyId, "PatientId": pB, "AllergenAdvRecName": "changed by B", "Severity": "Mild"})
check("B cannot take over A's allergy record by saying it belongs to B's patient", allergyId and denied(r) and "not under your care" in msg(r), r)
check("the allergy is unchanged", any("Penicillin " + SUFFIX in (x.get("AllergenAdvRecName") or "") for x in A.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)["Results"]))

section("B adds A's patient to their own care")
r = B.post("/api/CareTeam/AddToMyCare", {"PatientId": p1, "Note": "second opinion requested"})
check("B adds the patient to their care", ok(r) and r["Results"]["Added"], r)
check("now B can open the patient", ok(B.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)))
check("the patient is on B's list", any(x["PatientId"] == p1 for x in B.get("/api/CareTeam/MyPatients")["Results"]))
inboxA = A.get("/api/Messages/Inbox")["Results"]
check("A was told about it in their messages", any("added themselves" in (m.get("Preview") or "") and m["PatientId"] == p1 for m in inboxA), inboxA)
check("the team lists both doctors", len(A.get("/api/CareTeam/Team?patientId=%d" % p1)["Results"]) == 2)
check("the audit trail records B's search and B's denied attempts", any(l["Action"] in ("Search", "Denied") and l["Actor"] for l in admin.get("/api/DoctorAdmin/AccessLog")["Results"]))

section("Sharing and messages")
r = A.post("/api/CareTeam/RegisterPatient", dict(reg, FirstName="Sam", LastName="Gamma" + SUFFIX, Gender="Female", DateOfBirth="2015-01-02"))
p2 = r["Results"]["PatientId"] if ok(r) else 0
check("A registers a second patient", p2 > 0, r)
check("B cannot share a patient they don't have", denied(B.post("/api/CareTeam/Share", {"PatientId": p2, "ToEmployeeId": idA})))
r = A.post("/api/CareTeam/Share", {"PatientId": p2, "ToEmployeeId": idB, "Message": "Please take over the paediatric follow-up."})
check("A shares the patient with B", ok(r), r)
check("the shared patient is on B's list", any(x["PatientId"] == p2 for x in B.get("/api/CareTeam/MyPatients")["Results"]))
unread = B.get("/api/Messages/UnreadCount")["Results"]
check("B has unread messages (share notice)", unread >= 1, unread)
inbox = B.get("/api/Messages/Inbox")["Results"]
share_msg = next((m for m in inbox if m["MessageType"] == "PatientShare" and m["PatientId"] == p2), None)
check("B's inbox has the share notice for that patient", share_msg is not None, inbox)
full = B.get("/api/Messages/Message?messageId=%d" % share_msg["MessageId"]) if share_msg else {}
check("opening the message shows the note and marks it read", ok(full) and "paediatric follow-up" in full["Results"]["Body"] and full["Results"]["IsRead"], full)
r = A.post("/api/Messages/Send", {"ToEmployeeIds": [idB], "Subject": "Lab results", "Body": "Results for the patient are back.", "PatientId": p1})
check("A e-mails B about a patient", ok(r), r)
check("A cannot attach a patient who is not theirs", denied(A.post("/api/Messages/Send", {"ToEmployeeIds": [idB], "Subject": "x", "Body": "y", "PatientId": 1})))
check("an empty message is refused", denied(A.post("/api/Messages/Send", {"ToEmployeeIds": [idB], "Subject": "x", "Body": "  "})))
check("B sees the new message", any(m["Subject"] == "Lab results" for m in B.get("/api/Messages/Inbox")["Results"]))
check("A sees it in Sent", any(m["Subject"] == "Lab results" for m in A.get("/api/Messages/Sent")["Results"]))
mid = next(m["MessageId"] for m in B.get("/api/Messages/Inbox")["Results"] if m["Subject"] == "Lab results")
check("a third person (even the administrator) cannot read A's message to B", denied(admin.get("/api/Messages/Message?messageId=%d" % mid)) and denied(Client("x").get("/api/Messages/Message?messageId=%d" % mid)))
check("B deletes the message", ok(B.post("/api/Messages/Delete", {"MessageId": mid})))
check("... and it is gone from B's inbox", not any(m["MessageId"] == mid for m in B.get("/api/Messages/Inbox")["Results"]))
r = A.post("/api/CareTeam/RemoveMe", {"PatientId": p1})
check("A leaves patient 1's care team", ok(r))
check("A can no longer open that patient", denied(A.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)))
check("B (still on the team) can", ok(B.get("/api/Clinical/PatientAllergies?patientId=%d" % p1)))

# ---------------------------------------------------------------------------------------------------------------------
section("Administrator: change, reset, withdraw, re-instate")
r = admin.call("PUT", "/api/DoctorAdmin/Staff", {"UserId": uidB, "EmployeeId": idB, "Salutation": "Dr", "FirstName": "Test", "LastName": "Beta" + SUFFIX, "Gender": "Female",
                                                 "DepartmentId": depts[1]["DepartmentId"], "Speciality": "Neonatology", "RoleId": doctor_role, "Email": userB + "@example.org"})
check("admin changes B's speciality", ok(r), r)
check("... and it shows in the list", any(s["UserId"] == uidB and s["Speciality"] == "Neonatology" for s in admin.get("/api/DoctorAdmin/Staff")["Results"]))
check("B sees the new speciality in the directory", any(d["EmployeeId"] == idB and d["Speciality"] == "Neonatology" for d in A.get("/api/CareTeam/Directory")["Results"]))
r = admin.post("/api/DoctorAdmin/ResetPassword", {"UserId": uidA, "NewPassword": "brandNew" + SUFFIX, "MustChange": False})
check("admin resets A's password", ok(r), r)
check("A's old password no longer works", not Client("x").login(userA, pwA))
A2 = Client("A2")
check("A signs in with the new password", A2.login(userA, "brandNew" + SUFFIX))
check("the admin cannot withdraw themselves", denied(admin.post("/api/DoctorAdmin/Withdraw", {"UserId": 1})))

r = admin.post("/api/DoctorAdmin/Withdraw", {"UserId": uidB, "HandOverToEmployeeId": idA, "Reason": "left the hospital"})
check("admin withdraws B and hands the patients to A", ok(r), r)
check("B's session stops working immediately", denied(B.get("/api/CareTeam/MyPatients")))
check("B cannot sign in again through the form-less API either (token is useless)", denied(Client("x").get("/api/CareTeam/MyPatients", token=B.token)))
check("B has disappeared from the doctor directory", not any(d["EmployeeId"] == idB for d in A2.get("/api/CareTeam/Directory")["Results"]))
check("B's patients were handed to A", any(x["PatientId"] == p2 for x in A2.get("/api/CareTeam/MyPatients")["Results"]) and any(x["PatientId"] == p1 for x in A2.get("/api/CareTeam/MyPatients")["Results"]))
check("sharing with a withdrawn doctor is refused", denied(A2.post("/api/CareTeam/Share", {"PatientId": p2, "ToEmployeeId": idB})))
r = admin.post("/api/DoctorAdmin/Reactivate", {"UserId": uidB})
check("admin re-instates B", ok(r), r)
B2 = Client("B2")
check("B signs in again", B2.login(userB, pwB) and ok(B2.get("/api/CareTeam/MyPatients")))

section("Clean up: withdraw the test doctors")
check("withdraw A", ok(admin.post("/api/DoctorAdmin/Withdraw", {"UserId": uidA, "Reason": "test finished"})))
check("withdraw B", ok(admin.post("/api/DoctorAdmin/Withdraw", {"UserId": uidB, "Reason": "test finished"})))
check("withdraw D", ok(admin.post("/api/DoctorAdmin/Withdraw", {"UserId": uidD, "Reason": "test finished"})))
check("withdraw the clerk", uidF and ok(admin.post("/api/DoctorAdmin/Withdraw", {"UserId": uidF, "Reason": "test finished"})))

print("\n%d checks passed, %d failed" % (passed, len(failed)))
for f in failed:
    print("  FAILED: " + f)
sys.exit(1 if failed else 0)
