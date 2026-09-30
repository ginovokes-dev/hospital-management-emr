import { Component, EventEmitter, Input, OnInit, Output } from "@angular/core";
import { MessageboxService } from "../../shared/messagebox/messagebox.service";
import { CareTeamService } from "../care-team.service";

/** share a patient with another doctor: they are added to the care team and told by an in-application message */
@Component({
  selector: "ct-share-patient-dialog",
  templateUrl: "./share-patient-dialog.component.html",
  styleUrls: ["../care-team-common.css"]
})
export class SharePatientDialogComponent implements OnInit {
  @Input() patient: any;
  @Output() done = new EventEmitter<boolean>();

  public doctors: any[] = [];
  public toEmployeeId: number = null;
  public message: string = "";
  public loading: boolean = true;
  public saving: boolean = false;
  public error: string = null;

  constructor(private svc: CareTeamService, private msgBox: MessageboxService) { }

  ngOnInit() {
    this.svc.directory(true).subscribe(res => {
      this.loading = false;
      if (res.Status == "OK") {
        const already = (this.patient.Team || []).map(t => t.EmployeeId);
        this.doctors = (res.Results || []).filter(d => already.indexOf(d.EmployeeId) < 0);
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => { this.loading = false; this.error = "The list of doctors could not be loaded."; });
  }

  share() {
    this.error = null;
    if (!this.toEmployeeId) { this.error = "Please choose the doctor to share the patient with."; return; }
    this.saving = true;
    this.svc.share(this.patient.PatientId, +this.toEmployeeId, this.message).subscribe(res => {
      this.saving = false;
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [res.Results.Message]);
        this.done.emit(true);
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => { this.saving = false; this.error = "That did not work. Please try again."; });
  }

  cancel() {
    this.done.emit(false);
  }
}
