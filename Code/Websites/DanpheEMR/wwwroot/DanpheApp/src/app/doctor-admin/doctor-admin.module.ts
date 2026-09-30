import { NgModule } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { RouterModule } from "@angular/router";

import { AuthGuardService } from "../security/shared/auth-guard.service";
import { DoctorAdminService } from "./doctor-admin.service";
import { DoctorAdminComponent } from "./doctor-admin.component";

/** Manage Doctors: the administrator issues, changes and withdraws doctor logins (HttpClientModule not imported on purpose, see CareTeamModule) */
@NgModule({
  imports: [
    CommonModule,
    FormsModule,
    RouterModule.forChild([{ path: "", component: DoctorAdminComponent, canActivate: [AuthGuardService] }])
  ],
  providers: [DoctorAdminService],
  declarations: [DoctorAdminComponent]
})
export class DoctorAdminModule { }
