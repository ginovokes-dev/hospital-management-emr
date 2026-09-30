import { Injectable } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { Observable } from "rxjs";

/** Talks to api/DoctorAdmin (administrator only - the server refuses everybody else) */
@Injectable()
export class DoctorAdminService {
  constructor(private http: HttpClient) { }

  lookups(): Observable<any> { return this.http.get<any>("/api/DoctorAdmin/Lookups"); }
  staff(): Observable<any> { return this.http.get<any>("/api/DoctorAdmin/Staff"); }
  create(staff: any): Observable<any> { return this.http.post<any>("/api/DoctorAdmin/Staff", staff); }
  update(staff: any): Observable<any> { return this.http.put<any>("/api/DoctorAdmin/Staff", staff); }
  resetPassword(userId: number, newPassword: string, mustChange: boolean): Observable<any> {
    return this.http.post<any>("/api/DoctorAdmin/ResetPassword", { UserId: userId, NewPassword: newPassword, MustChange: mustChange });
  }
  withdraw(userId: number, handOverToEmployeeId: number, reason: string): Observable<any> {
    return this.http.post<any>("/api/DoctorAdmin/Withdraw", { UserId: userId, HandOverToEmployeeId: handOverToEmployeeId || null, Reason: reason });
  }
  reactivate(userId: number): Observable<any> { return this.http.post<any>("/api/DoctorAdmin/Reactivate", { UserId: userId }); }
  accessLog(): Observable<any> { return this.http.get<any>("/api/DoctorAdmin/AccessLog", { params: new HttpParams().set("take", "300") }); }
}
