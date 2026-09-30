import { Component, EventEmitter, Input, OnInit, Output } from "@angular/core";
import { MessageboxService } from "../../shared/messagebox/messagebox.service";
import { CareTeamService } from "../care-team.service";

/**
 * Write an in-application message to one or more colleagues, optionally about one of your patients.
 * Inputs: patient (attach this patient), toEmployeeIds + subject (a reply).
 */
@Component({
  selector: "ct-compose-message-dialog",
  templateUrl: "./compose-message-dialog.component.html",
  styleUrls: ["../care-team-common.css"]
})
export class ComposeMessageDialogComponent implements OnInit {
  @Input() patient: any = null;
  @Input() toEmployeeIds: number[] = [];
  @Input() subject: string = "";
  @Output() done = new EventEmitter<boolean>();

  public people: any[] = [];
  public myPatients: any[] = [];
  public selected: { [id: number]: boolean } = {};
  public filter: string = "";
  public body: string = "";
  public attachPatientId: number = null;
  public loading: boolean = true;
  public sending: boolean = false;
  public error: string = null;

  constructor(private svc: CareTeamService, private msgBox: MessageboxService) { }

  ngOnInit() {
    (this.toEmployeeIds || []).forEach(id => this.selected[id] = true);
    if (this.patient) {
      this.attachPatientId = this.patient.PatientId;
      if (!this.subject) { this.subject = "About " + this.patient.Name + " (" + this.patient.PatientCode + ")"; }
    }
    this.svc.people().subscribe(res => {
      this.loading = false;
      if (res.Status == "OK") { this.people = res.Results || []; } else { this.error = res.ErrorMessage; }
    }, () => { this.loading = false; this.error = "The list of colleagues could not be loaded."; });
    this.svc.myPatients("").subscribe(res => {
      if (res.Status == "OK") { this.myPatients = res.Results || []; }
    }, () => { });
  }

  get visiblePeople(): any[] {
    const f = (this.filter || "").trim().toLowerCase();
    return f ? this.people.filter(p => (p.Name + " " + (p.Speciality || "") + " " + (p.RoleName || "")).toLowerCase().indexOf(f) >= 0) : this.people;
  }

  get chosenIds(): number[] {
    return Object.keys(this.selected).filter(k => this.selected[+k]).map(k => +k);
  }

  send() {
    this.error = null;
    if (this.chosenIds.length == 0) { this.error = "Please choose who the message is for."; return; }
    if (!this.body || !this.body.trim()) { this.error = "Please write a message."; return; }
    this.sending = true;
    this.svc.send(this.chosenIds, this.subject, this.body, this.attachPatientId).subscribe(res => {
      this.sending = false;
      if (res.Status == "OK") {
        this.msgBox.showMessage("success", [res.Results]);
        this.done.emit(true);
      } else {
        this.error = res.ErrorMessage;
      }
    }, () => { this.sending = false; this.error = "The message could not be sent. Please try again."; });
  }

  cancel() {
    this.done.emit(false);
  }
}
