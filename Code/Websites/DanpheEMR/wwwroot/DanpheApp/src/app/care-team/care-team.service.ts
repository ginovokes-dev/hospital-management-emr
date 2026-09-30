import { Injectable } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { BehaviorSubject, Observable } from "rxjs";

/**
 * Talks to the Care Team, Messages and Doctor-admin APIs (see CareTeam/CareTeamControllers.cs). Every answer has the application's
 * usual shape { Status: "OK" | "Failed", Results, ErrorMessage }. The sign-in token is added by the application's HTTP interceptor.
 */
@Injectable()
export class CareTeamService {
  /** number of unread messages, shown next to "Messages" in the tab bar */
  public unread$ = new BehaviorSubject<number>(0);

  constructor(private http: HttpClient) { }

  refreshUnread(): void {
    this.unreadCount().subscribe(r => { if (r && r.Status == "OK") { this.unread$.next(+r.Results || 0); } }, () => { });
  }

  // ---- care team ----
  summary(): Observable<any> { return this.http.get<any>("/api/CareTeam/Summary"); }
  myPatients(search: string = ""): Observable<any> {
    return this.http.get<any>("/api/CareTeam/MyPatients", { params: new HttpParams().set("search", search || "") });
  }
  findPatient(search: string): Observable<any> {
    return this.http.get<any>("/api/CareTeam/FindPatient", { params: new HttpParams().set("search", search || "") });
  }
  directory(doctorsOnly: boolean): Observable<any> {
    return this.http.get<any>("/api/CareTeam/Directory", { params: new HttpParams().set("doctorsOnly", doctorsOnly ? "true" : "false") });
  }
  team(patientId: number): Observable<any> {
    return this.http.get<any>("/api/CareTeam/Team", { params: new HttpParams().set("patientId", String(patientId)) });
  }
  addToMyCare(patientId: number, note: string): Observable<any> {
    return this.http.post<any>("/api/CareTeam/AddToMyCare", { PatientId: patientId, Note: note });
  }
  share(patientId: number, toEmployeeId: number, message: string): Observable<any> {
    return this.http.post<any>("/api/CareTeam/Share", { PatientId: patientId, ToEmployeeId: toEmployeeId, Message: message });
  }
  removeMe(patientId: number): Observable<any> {
    return this.http.post<any>("/api/CareTeam/RemoveMe", { PatientId: patientId });
  }
  registerPatient(patient: any): Observable<any> {
    return this.http.post<any>("/api/CareTeam/RegisterPatient", patient);
  }
  openChart(patientId: number, newConsultation: boolean): Observable<any> {
    return this.http.post<any>("/api/CareTeam/OpenChart", { PatientId: patientId, NewConsultation: newConsultation });
  }

  // ---- messages ----
  unreadCount(): Observable<any> { return this.http.get<any>("/api/Messages/UnreadCount"); }
  inbox(): Observable<any> { return this.http.get<any>("/api/Messages/Inbox", { params: new HttpParams().set("take", "100") }); }
  sent(): Observable<any> { return this.http.get<any>("/api/Messages/Sent", { params: new HttpParams().set("take", "100") }); }
  message(messageId: number): Observable<any> {
    return this.http.get<any>("/api/Messages/Message", { params: new HttpParams().set("messageId", String(messageId)) });
  }
  people(): Observable<any> { return this.http.get<any>("/api/Messages/People"); }
  send(toEmployeeIds: number[], subject: string, body: string, patientId: number): Observable<any> {
    return this.http.post<any>("/api/Messages/Send", { ToEmployeeIds: toEmployeeIds, Subject: subject, Body: body, PatientId: patientId || null });
  }
  deleteMessage(messageId: number): Observable<any> { return this.http.post<any>("/api/Messages/Delete", { MessageId: messageId }); }
  markAllRead(): Observable<any> { return this.http.post<any>("/api/Messages/MarkAllRead", {}); }
}
