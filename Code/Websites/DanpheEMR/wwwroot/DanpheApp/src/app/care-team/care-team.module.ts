import { NgModule } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { RouterModule } from "@angular/router";

import { CareTeamRoutingConstant } from "./care-team-routing.constant";
import { CareTeamService } from "./care-team.service";
import { ChartOpener } from "./chart-opener.service";
import { CareTeamMainComponent } from "./care-team-main.component";
import { MyPatientsComponent } from "./my-patients.component";
import { FindPatientComponent } from "./find-patient.component";
import { MessagesComponent } from "./messages.component";
import { RegisterPatientDialogComponent } from "./dialogs/register-patient-dialog.component";
import { SharePatientDialogComponent } from "./dialogs/share-patient-dialog.component";
import { ComposeMessageDialogComponent } from "./dialogs/compose-message-dialog.component";

/**
 * Care Team: the doctor's own patient list, finding a patient who is under another doctor, adding yourself to a patient's
 * care, sharing a patient with a colleague, and in-application messages between staff.
 * (HttpClientModule is deliberately not imported here: the application-wide HttpClient carries the sign-in token.)
 */
@NgModule({
  imports: [CommonModule, FormsModule, RouterModule.forChild(CareTeamRoutingConstant)],
  providers: [CareTeamService, ChartOpener],
  declarations: [
    CareTeamMainComponent,
    MyPatientsComponent,
    FindPatientComponent,
    MessagesComponent,
    RegisterPatientDialogComponent,
    SharePatientDialogComponent,
    ComposeMessageDialogComponent
  ]
})
export class CareTeamModule { }
