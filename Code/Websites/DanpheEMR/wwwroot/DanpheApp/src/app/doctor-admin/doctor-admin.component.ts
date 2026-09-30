import { Component, OnInit } from "@angular/core";
import { MessageboxService } from "../shared/messagebox/messagebox.service";
import { DoctorAdminService } from "./doctor-admin.service";

/**
 * Manage Doctors (administrator only): add a doctor with speciality, role and login; change details; reset a password;
 * withdraw a doctor (login switched off, nothing deleted, patients can be handed over) and bring them back; see who looked at what.
 */
@Component({
  templateUrl: "./doctor-admin.component.html",
  styleUrls: ["../care-team/care-team-common.css", "./doctor-admin.component.css"]
})
export class DoctorAdminComponent implements OnInit {
  public tab: string = "staff";
  public staff: any[] = [];
  public filter: string = "";
  public showWithdrawn: boolean = false;
  public loading: boolean = false;
  public error: string = null;

  public roles: any[] = [];
  public departments: any[] = [];
  public salutations: string[] = [];
  public specialities: string[] = [];

  // add / edit
  public editing: any = null;               // the form model, null = dialog closed
  public isNew: boolean = false;
  public saving: boolean = false;
  public formError: string = null;
  public showPassword: boolean = false;
  public issued: any = null;                // { name, userName, password } shown once after a new login was created

  // reset password
  public resetTarget: any = null;
  public resetPassword: string = "";
  public resetMustChange: boolean = true;
  public resetError: string = null;

  // withdraw
  public withdrawTarget: any = null;
  public withdrawReason: string = "";
  public handOverTo: number = null;
  public withdrawError: string = null;

  public log: any[] = [];

  constructor(private svc: DoctorAdminService, private msgBox: MessageboxService) { }

  ngOnInit() {
    this.svc.lookups().subscribe(res => {
      if (res.Status == "OK") {
        this.roles = res.Results.Roles;
        this.departments = res.Results.Departments;
        this.salutations = res.Results.Salutations;
        this.specialities = res.Results.Specialities;
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => { this.error = "The page could not be loaded. Please try again."; });
    this.load();
  }

  load() {
    this.loading = true;
    this.svc.staff().subscribe(res => {
      this.loading = false;
      if (res.Status == "OK") { this.staff = res.Results || []; } else { this.error = res.ErrorMessage; }
    }, () => { this.loading = false; this.error = "The list could not be loaded. Please try again."; });
  }

  get visibleStaff(): any[] {
    const f = (this.filter || "").trim().toLowerCase();
    return this.staff.filter(s => {
      if (!this.showWithdrawn && !(s.LoginActive && s.EmployeeActive)) { return false; }
      if (!f) { return true; }
      return ((s.FullName || "") + " " + (s.UserName || "") + " " + (s.Speciality || "") + " " + (s.DepartmentName || "") + " " + (s.RoleName || "")).toLowerCase().indexOf(f) >= 0;
    });
  }

  isActive(s: any): boolean { return !!(s.LoginActive && s.EmployeeActive); }

  get activeDoctors(): any[] {
    return this.staff.filter(s => this.isActive(s) && s.IsDoctorRole && (!this.withdrawTarget || s.UserId != this.withdrawTarget.UserId));
  }

  // ---- add / edit ----
  add() {
    this.isNew = true;
    this.formError = null;
    this.showPassword = true;
    const defaultRole = this.roles.find(r => r.IsDoctorRole && r.RoleName == "Doctor") || this.roles.find(r => r.IsDoctorRole) || this.roles[0];
    this.editing = {
      Salutation: "Dr", FirstName: "", MiddleName: "", LastName: "", Gender: "", Email: "", ContactNumber: "", DepartmentId: null, Speciality: "",
      MedCertificationNo: "", RoleId: defaultRole ? defaultRole.RoleId : null, UserName: "", Password: this.generatePassword(), MustChangePassword: true
    };
  }

  edit(s: any) {
    this.isNew = false;
    this.formError = null;
    this.editing = {
      UserId: s.UserId, EmployeeId: s.EmployeeId, UserName: s.UserName, IsSuperAdmin: s.IsSuperAdmin,
      Salutation: s.Salutation, FirstName: s.FirstName, MiddleName: s.MiddleName, LastName: s.LastName, Gender: s.Gender, Email: s.Email,
      ContactNumber: s.ContactNumber, DepartmentId: s.DepartmentId, Speciality: s.Speciality, MedCertificationNo: s.MedCertificationNo, RoleId: s.RoleId
    };
  }

  get roleIsDoctor(): boolean {
    if (!this.editing) { return false; }
    const r = this.roles.find(x => x.RoleId == this.editing.RoleId);
    return this.editing.IsSuperAdmin ? false : !!(r && r.IsDoctorRole);
  }

  onDepartmentChange() {
    // suggest the department's name as the speciality while the speciality is still empty
    if (this.editing && !this.editing.Speciality && this.editing.DepartmentId) {
      const d = this.departments.find(x => x.DepartmentId == this.editing.DepartmentId);
      if (d) { this.editing.Speciality = d.DepartmentName; }
    }
  }

  generatePassword(): string {
    const chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    let out = "";
    const c = (window as any).crypto || (window as any).msCrypto;
    const bytes = new Uint32Array(10);
    if (c && c.getRandomValues) { c.getRandomValues(bytes); } else { for (let i = 0; i < 10; i++) { bytes[i] = Math.floor(Math.random() * 4294967295); } }
    for (let i = 0; i < 10; i++) { out += chars.charAt(bytes[i] % chars.length); }
    return out;
  }

  newPassword() {
    this.editing.Password = this.generatePassword();
    this.showPassword = true;
  }

  save() {
    this.formError = null;
    const e = this.editing;
    if (!e.FirstName || !e.FirstName.trim()) { this.formError = "Please enter the first name."; return; }
    if (!e.LastName || !e.LastName.trim()) { this.formError = "Please enter the last name."; return; }
    if (!e.IsSuperAdmin && !e.RoleId) { this.formError = "Please choose a role."; return; }
    if (this.roleIsDoctor && !e.DepartmentId) { this.formError = "Please choose the doctor's speciality / department."; return; }
    if (this.isNew) {
      if (!/^[A-Za-z0-9._-]{3,30}$/.test(e.UserName || "")) { this.formError = "The username must be 3-30 letters, numbers, dots, dashes or underscores (no spaces)."; return; }
      if (!e.Password || e.Password.length < 6 || e.Password.length > 20) { this.formError = "The password must be 6 to 20 characters long."; return; }
    }
    this.saving = true;
    const call = this.isNew ? this.svc.create(e) : this.svc.update(e);
    call.subscribe(res => {
      this.saving = false;
      if (res.Status == "OK") {
        if (this.isNew) {
          this.issued = { name: ((e.Salutation ? e.Salutation + ". " : "") + e.FirstName + " " + e.LastName).trim(), userName: e.UserName, password: e.Password, mustChange: e.MustChangePassword };
        } else {
          this.msgBox.showMessage("success", ["Saved."]);
        }
        this.editing = null;
        this.load();
      } else {
        this.formError = res.ErrorMessage;
      }
    }, () => { this.saving = false; this.formError = "That did not work. Please try again."; });
  }

  // ---- reset password ----
  startReset(s: any) {
    this.resetTarget = s;
    this.resetPassword = this.generatePassword();
    this.resetMustChange = true;
    this.resetError = null;
  }

  confirmReset() {
    this.resetError = null;
    if (!this.resetPassword || this.resetPassword.length < 6 || this.resetPassword.length > 20) { this.resetError = "The password must be 6 to 20 characters long."; return; }
    const t = this.resetTarget;
    this.svc.resetPassword(t.UserId, this.resetPassword, this.resetMustChange).subscribe(res => {
      if (res.Status == "OK") {
        this.issued = { name: t.FullName, userName: t.UserName, password: this.resetPassword, mustChange: this.resetMustChange, reset: true };
        this.resetTarget = null;
        this.load();
      } else {
        this.resetError = res.ErrorMessage;
      }
    }, () => { this.resetError = "That did not work. Please try again."; });
  }

  // ---- withdraw / re-activate ----
  startWithdraw(s: any) {
    this.withdrawTarget = s;
    this.withdrawReason = "";
    this.handOverTo = null;
    this.withdrawError = null;
  }

  confirmWithdraw() {
    const t = this.withdrawTarget;
    this.svc.withdraw(t.UserId, this.handOverTo, this.withdrawReason).subscribe(res => {
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [t.FullName + " has been withdrawn. The login no longer works."]);
        this.withdrawTarget = null;
        this.load();
      } else {
        this.withdrawError = res.ErrorMessage;
      }
    }, () => { this.withdrawError = "That did not work. Please try again."; });
  }

  reactivate(s: any) {
    this.svc.reactivate(s.UserId).subscribe(res => {
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [s.FullName + " can sign in again."]);
        this.load();
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => this.msgBox.showMessage("failed", ["That did not work. Please try again."]));
  }

  // ---- log ----
  showTab(t: string) {
    this.tab = t;
    if (t == "log") {
      this.svc.accessLog().subscribe(res => {
        if (res.Status == "OK") { this.log = res.Results || []; } else { this.error = res.ErrorMessage; }
      }, () => { this.error = "The activity log could not be loaded."; });
    }
  }

  actionLabel(a: string): string {
    const labels = {
      Search: "Searched for a patient", AddSelf: "Added a patient to their care", Share: "Shared a patient", RemoveSelf: "Removed a patient from their list",
      Register: "Registered a patient", StartVisit: "Started a consultation", MessagePatient: "Messaged about a patient", Denied: "Was refused access to a patient",
      AdminAddDoctor: "Administrator added a login", AdminEditDoctor: "Administrator changed a login", AdminResetPassword: "Administrator reset a password",
      AdminWithdraw: "Administrator withdrew a login", AdminReactivate: "Administrator re-activated a login"
    };
    return (labels as any)[a] || a;
  }

  copyIssued() {
    const i = this.issued;
    const text = "Username: " + i.userName + "\nPassword: " + i.password;
    const el = document.createElement("textarea");
    el.value = text;
    document.body.appendChild(el);
    el.select();
    try { document.execCommand("copy"); this.msgBox.showMessage("success", ["Copied."]); } catch (e) { /* the login is shown on screen anyway */ }
    document.body.removeChild(el);
  }
}
