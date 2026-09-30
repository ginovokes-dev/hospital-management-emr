import { Component, EventEmitter, Output } from "@angular/core";
import { CareTeamService } from "../care-team.service";

/** a short registration form for a new patient; the registering doctor is put on the patient's care team automatically */
@Component({
  selector: "ct-register-patient-dialog",
  templateUrl: "./register-patient-dialog.component.html",
  styleUrls: ["../care-team-common.css"]
})
export class RegisterPatientDialogComponent {
  @Output() done = new EventEmitter<any>();

  public salutations: string[] = ["Mr", "Mrs", "Ms", "Miss", "Master", "Baby", "Dr", "Prof"];
  public bloodGroups: string[] = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];
  public p: any = { Salutation: "", FirstName: "", MiddleName: "", LastName: "", Gender: "", DateOfBirth: "", PhoneNumber: "", Address: "", Email: "", BloodGroup: "" };
  public saving: boolean = false;
  public error: string = null;
  public today: string = new Date().toISOString().substring(0, 10);

  constructor(private svc: CareTeamService) { }

  save() {
    this.error = null;
    const p = this.p;
    if (!p.FirstName || !p.FirstName.trim()) { this.error = "Please enter the patient's first name."; return; }
    if (!p.LastName || !p.LastName.trim()) { this.error = "Please enter the patient's last name."; return; }
    if (!p.Gender) { this.error = "Please choose the patient's gender."; return; }
    if (!p.DateOfBirth) { this.error = "Please enter the date of birth (year-month-day)."; return; }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(p.DateOfBirth) || isNaN(Date.parse(p.DateOfBirth))) { this.error = "The date of birth must look like 1985-03-27."; return; }
    if (p.DateOfBirth > this.today) { this.error = "The date of birth cannot be in the future."; return; }
    this.saving = true;
    this.svc.registerPatient(p).subscribe(res => {
      this.saving = false;
      if (res.Status == "OK") {
        this.done.emit(res.Results);
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => { this.saving = false; this.error = "The patient could not be saved. Please try again."; });
  }

  cancel() {
    this.done.emit(null);
  }
}
