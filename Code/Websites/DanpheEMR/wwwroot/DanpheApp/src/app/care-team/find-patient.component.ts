import { Component, OnInit } from "@angular/core";
import { MessageboxService } from "../shared/messagebox/messagebox.service";
import { CareTeamService } from "./care-team.service";
import { ChartOpener } from "./chart-opener.service";

/**
 * Look a patient up by name / patient number, see which doctor(s) the patient is under, and add yourself to the patient's care.
 * Only name, patient number, sex and age are shown before the patient is on your list. Every search is written to the audit log.
 */
@Component({
  templateUrl: "./find-patient.component.html",
  styleUrls: ["./care-team-common.css"]
})
export class FindPatientComponent implements OnInit {
  public search: string = "";
  public results: any[] = null;             // null = nothing searched yet
  public busy: boolean = false;
  public error: string = null;
  public opening: number = 0;

  public addTarget: any = null;
  public addNote: string = "";
  public adding: boolean = false;
  public shareTarget: any = null;

  constructor(private svc: CareTeamService, private msgBox: MessageboxService, private opener: ChartOpener) { }

  ngOnInit() { }

  run() {
    this.error = null;
    if (!this.search || this.search.trim().length < 2) {
      this.error = "Type at least two letters of the patient's name, or the patient number.";
      return;
    }
    this.busy = true;
    this.svc.findPatient(this.search.trim()).subscribe(res => {
      this.busy = false;
      if (res.Status == "OK") {
        this.results = res.Results || [];
      } else {
        this.results = null;
        this.error = res.ErrorMessage;
      }
    }, () => { this.busy = false; this.error = "The search did not work. Please try again."; });
  }

  openChart(p: any) {
    this.opening = p.PatientId;
    this.opener.open(p.PatientId, false, () => { this.opening = 0; });
  }

  confirmAdd() {
    this.adding = true;
    const p = this.addTarget;
    this.svc.addToMyCare(p.PatientId, this.addNote).subscribe(res => {
      this.adding = false;
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [p.Name + " is now on your list. The other doctors on the care team have been told."]);
        this.addTarget = null;
        this.addNote = "";
        this.run();
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => { this.adding = false; this.msgBox.showMessage("failed", ["That did not work. Please try again."]); });
  }

  startAdd(p: any) {
    this.addTarget = p;
    this.addNote = "";
  }

  shared(done: boolean) {
    this.shareTarget = null;
    if (done) { this.run(); }
  }
}
