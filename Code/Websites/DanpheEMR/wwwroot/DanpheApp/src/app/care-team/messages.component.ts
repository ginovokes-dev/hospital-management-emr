import { Component, OnInit } from "@angular/core";
import { MessageboxService } from "../shared/messagebox/messagebox.service";
import { CareTeamService } from "./care-team.service";
import { ChartOpener } from "./chart-opener.service";

/** in-application "e-mail" between staff: inbox, sent, reading, replying, and jumping to the patient a message is about */
@Component({
  templateUrl: "./messages.component.html",
  styleUrls: ["./care-team-common.css"]
})
export class MessagesComponent implements OnInit {
  public folder: string = "inbox";
  public messages: any[] = [];
  public selected: any = null;             // the message shown on the right (full text)
  public loading: boolean = false;
  public error: string = null;
  public opening: boolean = false;

  public showCompose: boolean = false;
  public replyTo: number[] = [];
  public replySubject: string = "";
  public replyPatient: any = null;
  public adding: boolean = false;

  constructor(public svc: CareTeamService, private msgBox: MessageboxService, private opener: ChartOpener) { }

  ngOnInit() {
    this.load();
  }

  show(folder: string) {
    this.folder = folder;
    this.selected = null;
    this.load();
  }

  load() {
    this.loading = true;
    this.error = null;
    const call = this.folder == "inbox" ? this.svc.inbox() : this.svc.sent();
    call.subscribe(res => {
      this.loading = false;
      if (res.Status == "OK") {
        this.messages = res.Results || [];
      } else {
        this.error = res.ErrorMessage;
      }
      this.svc.refreshUnread();
    }, () => { this.loading = false; this.error = "The messages could not be loaded. Please try again."; });
  }

  open(m: any) {
    this.svc.message(m.MessageId).subscribe(res => {
      if (res.Status == "OK") {
        this.selected = res.Results;
        if (this.folder == "inbox") { m.IsRead = true; this.svc.refreshUnread(); }
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => this.msgBox.showMessage("failed", ["The message could not be opened."]));
  }

  compose() {
    this.replyTo = [];
    this.replySubject = "";
    this.replyPatient = null;
    this.showCompose = true;
  }

  reply() {
    const m = this.selected;
    this.replyTo = [m.FromEmployeeId == m.ToEmployeeId ? m.ToEmployeeId : (this.folder == "inbox" ? m.FromEmployeeId : m.ToEmployeeId)];
    this.replySubject = /^re:/i.test(m.Subject || "") ? m.Subject : "Re: " + (m.Subject || "");
    this.replyPatient = m.PatientId && m.PatientOnMyTeam ? { PatientId: m.PatientId, Name: m.PatientName, PatientCode: m.PatientCode } : null;
    this.showCompose = true;
  }

  composed(sent: boolean) {
    this.showCompose = false;
    if (sent && this.folder == "sent") { this.load(); }
  }

  remove() {
    const m = this.selected;
    this.svc.deleteMessage(m.MessageId).subscribe(res => {
      if (res.Status == "OK") {
        this.messages = this.messages.filter(x => x.MessageId != m.MessageId);
        this.selected = null;
        this.svc.refreshUnread();
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => this.msgBox.showMessage("failed", ["The message could not be deleted."]));
  }

  markAllRead() {
    this.svc.markAllRead().subscribe(res => {
      if (res.Status == "OK") { this.messages.forEach(m => m.IsRead = true); this.svc.refreshUnread(); }
    }, () => { });
  }

  openPatient() {
    this.opening = true;
    this.opener.open(this.selected.PatientId, false, () => { this.opening = false; });
  }

  addPatientToMyCare() {
    this.adding = true;
    this.svc.addToMyCare(this.selected.PatientId, "From a message").subscribe(res => {
      this.adding = false;
      if (res.Status == "OK") {
        this.selected.PatientOnMyTeam = true;
        this.msgBox.showMessage("success", ["The patient is now on your list."]);
      } else {
        this.msgBox.showMessage("failed", [res.ErrorMessage]);
      }
    }, () => { this.adding = false; this.msgBox.showMessage("failed", ["That did not work. Please try again."]); });
  }

  who(m: any): string {
    return this.folder == "inbox" ? m.FromName : "To: " + m.ToName;
  }

  typeLabel(m: any): string {
    return m.MessageType == "PatientShare" ? "Patient shared" : (m.MessageType == "CareUpdate" ? "Care update" : "");
  }
}
