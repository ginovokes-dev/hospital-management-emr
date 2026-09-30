import { Component, OnDestroy, OnInit } from "@angular/core";
import { SecurityService } from "../security/shared/security.service";
import { CareTeamService } from "./care-team.service";

/** the tab bar of the Care Team screens: My Patients | Find Patient | Messages (tabs come from the user's menu rights) */
@Component({
  templateUrl: "./care-team-main.component.html",
  styleUrls: ["./care-team-common.css"]
})
export class CareTeamMainComponent implements OnInit, OnDestroy {
  public validRoutes: any[] = [];
  private timer: any;

  constructor(private securityService: SecurityService, public svc: CareTeamService) {
    this.validRoutes = this.securityService.GetChildRoutes("CareTeam") || [];
  }

  ngOnInit() {
    this.svc.refreshUnread();
    this.timer = setInterval(() => this.svc.refreshUnread(), 45000);
  }

  ngOnDestroy() {
    clearInterval(this.timer);
  }
}
