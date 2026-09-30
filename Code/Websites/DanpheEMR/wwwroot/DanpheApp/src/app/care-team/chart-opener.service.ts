import { Injectable } from "@angular/core";
import { Router } from "@angular/router";
import * as moment from "moment/moment";
import { PatientService } from "../patients/shared/patient.service";
import { VisitService } from "../appointments/shared/visit.service";
import { MessageboxService } from "../shared/messagebox/messagebox.service";
import { SecurityService } from "../security/shared/security.service";
import { CareTeamService } from "./care-team.service";

/**
 * Opens a patient's record in the application's own Doctors screens. Those screens work on "the selected patient + visit"
 * (PatientService / VisitService), so this sets exactly what the Doctors > New Patient list sets when a row is clicked.
 */
@Injectable()
export class ChartOpener {
  constructor(private svc: CareTeamService, private router: Router, private msgBox: MessageboxService,
    private patientService: PatientService, private visitService: VisitService, private securityService: SecurityService) { }

  /** newConsultation: start a consultation for today even if the patient has earlier visits with this doctor */
  open(patientId: number, newConsultation: boolean = false, done: () => void = null): void {
    this.svc.openChart(patientId, newConsultation).subscribe(res => {
      if (!res || res.Status != "OK") {
        this.msgBox.showMessage("failed", [(res && res.ErrorMessage) || "The patient's record could not be opened."]);
        if (done) { done(); }
        return;
      }
      const d = res.Results, pt = d.Patient, v = d.Visit;
      const pat = this.patientService.CreateNewGlobal();
      pat.PatientId = pt.PatientId;
      pat.PatientCode = pt.PatientCode;
      pat.Salutation = pt.Salutation;
      pat.FirstName = pt.FirstName;
      pat.MiddleName = pt.MiddleName;
      pat.LastName = pt.LastName;
      pat.ShortName = pt.ShortName || pt.Name;
      pat.Gender = pt.Gender;
      pat.DateOfBirth = pt.DateOfBirth ? moment(pt.DateOfBirth).format("YYYY-MM-DD") : null;
      pat.Age = pt.Age;
      pat.PhoneNumber = pt.PhoneNumber;
      pat.Address = pt.Address;
      pat.EMPI = pt.EMPI;

      const vis = this.visitService.CreateNewGlobal();
      vis.PatientId = pt.PatientId;
      vis.PatientVisitId = v.PatientVisitId;
      vis.VisitCode = v.VisitCode;
      vis.PerformerId = this.securityService.GetLoggedInUser().EmployeeId;
      vis.PerformerName = d.PerformerName;
      vis.VisitDate = moment(v.VisitDate).format("YYYY-MM-DD");
      vis.VisitType = "outpatient";
      vis.VisitStatus = v.VisitStatus;
      vis.ConcludeDate = v.ConcludeDate ? moment(v.ConcludeDate).format("YYYY-MM-DD") : null;

      if (done) { done(); }
      this.router.navigate(["/Doctors/PatientOverviewMain/PatientOverview"]);
    }, () => {
      this.msgBox.showMessage("failed", ["The patient's record could not be opened."]);
      if (done) { done(); }
    });
  }
}
