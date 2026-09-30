import { AuthGuardService } from "../security/shared/auth-guard.service";
import { CareTeamMainComponent } from "./care-team-main.component";
import { FindPatientComponent } from "./find-patient.component";
import { MessagesComponent } from "./messages.component";
import { MyPatientsComponent } from "./my-patients.component";

export const CareTeamRoutingConstant = [
  {
    path: "",
    component: CareTeamMainComponent,
    canActivate: [AuthGuardService],
    children: [
      { path: "", redirectTo: "MyPatients", pathMatch: "full" },
      { path: "MyPatients", component: MyPatientsComponent, canActivate: [AuthGuardService] },
      { path: "FindPatient", component: FindPatientComponent, canActivate: [AuthGuardService] },
      { path: "Messages", component: MessagesComponent, canActivate: [AuthGuardService] }
    ]
  }
];
