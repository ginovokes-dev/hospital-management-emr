import { Injectable } from '@angular/core';
import { CanActivate, Router, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { User } from './user.model';
import { SecurityService } from './security.service';
import { CoreService } from '../../core/shared/core.service';
@Injectable()
export class AuthGuardService implements CanActivate {

  public loggedInUser: User = new User();
  constructor(public _router: Router, public securityServ: SecurityService, public coreService: CoreService) {
  }
  canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot): boolean | Promise<boolean> {
    this.coreService.loading = false;
    // state.url return current routing url like '/Billing/Transaction'.
    let url: string = state.url;
    if (this.isReady()) {
      return this.decide(url);
    }
    // After a page reload (F5) or when a link is opened directly, the signed-in user and the menu are still being fetched
    // from the server. Wait for them (instead of refusing the page and leaving a blank screen) - but not for ever.
    return new Promise<boolean>(resolve => {
      let waitedMs = 0;
      const timer = setInterval(() => {
        waitedMs += 100;
        if (this.isReady()) {
          clearInterval(timer);
          resolve(this.decide(url));
        } else if (waitedMs >= 20000) {
          clearInterval(timer);
          resolve(false);
        }
      }, 100);
    });
  }

  private isReady(): boolean {
    return this.securityServ.GetLoggedInUser().UserName != null;
  }

  private decide(url: string): boolean {
    this.loggedInUser = this.securityServ.GetLoggedInUser();
    this.coreService.currSelectedSecRoute = null;
    if (this.loggedInUser.UserName != null) {
      if (this.securityServ.checkIsAuthorizedURL(url)) {
        return true;
      } else {
        this._router.navigate(['/UnAuthorized']);// We are navigating unauthorized user.
        return false;
      }
    }
    return false;
  }
} 
