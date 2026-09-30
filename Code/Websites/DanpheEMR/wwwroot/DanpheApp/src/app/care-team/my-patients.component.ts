import { Component, OnInit } from "@angular/core";
import { MessageboxService } from "../shared/messagebox/messagebox.service";
import { CareTeamService } from "./care-team.service";
import { ChartOpener } from "./chart-opener.service";

/** the patients that are under this doctor's care */
@Component({
  templateUrl: "./my-patients.component.html",
  styleUrls: ["./care-team-common.css"]
})
export class MyPatientsComponent implements OnInit {
  public patients: any[] = [];
  public search: string = "";
  public loading: boolean = false;
  public error: string = null;
  public opening: number = 0;               // patient whose record is being opened

  public showRegister: boolean = false;
  public shareTarget: any = null;
  public messageTarget: any = null;
  public leaveTarget: any = null;
  private typing: any;

  constructor(private svc: CareTeamService, private msgBox: MessageboxService, private opener: ChartOpener) { }

  ngOnInit() {
    this.load();
  }

  load() {
    this.loading = true;
    this.error = null;
    this.svc.myPatients(this.search).subscribe(res => {
      this.loading = false;
      if (res.Status == "OK") {
        this.patients = res.Results || [];
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => {
      this.loading = false;
      this.error = "The list could not be loaded. Please check the connection and try again.";
    });
  }

  onSearchChange() {
    clearTimeout(this.typing);
    this.typing = setTimeout(() => this.load(), 300);
  }

  openChart(p: any, newConsultation: boolean = false) {
    this.opening = p.PatientId;
    this.opener.open(p.PatientId, newConsultation, () => { this.opening = 0; });
  }

  registered(patient: any) {
    this.showRegister = false;
    if (patient) {
      this.msgBox.showMessage("success", ["Patient " + patient.Name + " (" + patient.PatientCode + ") was registered and is on your list."]);
      this.load();
    }
  }

  shared(done: boolean) {
    this.shareTarget = null;
    if (done) { this.load(); }
  }

  confirmLeave() {
    const p = this.leaveTarget;
    this.svc.removeMe(p.PatientId).subscribe(res => {
      this.leaveTarget = null;
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [p.Name + " is no longer on your list."]);
        this.load();
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => { this.leaveTarget = null; this.msgBox.showMessage("failed", ["That did not work. Please try again."]); });
  }

  teamNames(p: any): string {
    return (p.Team || []).map(t => t.FullName + (t.Speciality ? " (" + t.Speciality + ")" : "")).join(", ");
  }
}
