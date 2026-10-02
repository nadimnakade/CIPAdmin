// ============================================================================
// CIPPortal (MVC 4.5.2) — SSO hand-off to the new CIP Admin (.NET 8 React app)
//
// Both apps share the CIP database. After CIP_LoginController.Chk_Credential
// succeeds (and the Google Authenticator step completes), mint a one-time
// ticket row and redirect the browser to the new app:  /?sso={ticket}
// The new app exchanges the ticket for its own JWT at GET /api/auth/sso.
//
// WHAT TO CHANGE IN CIPPortal:
//   1. Add this helper + the two action methods below to CIP_LoginController
//      (or better: call MintAndRedirect from the Verify controller's success
//      path so the ticket is only minted AFTER the OTP step passes).
//   2. Add a "Open CIP Admin" link/button anywhere in CIPPortal's chrome that
//      points at /CIP_Login/GoToCipAdmin.
//   3. web.config: nothing to add. No machineKey sharing, no session sharing.
//
// SECURITY NOTES
//   - Tickets are single-use, 60-second expiry, 43-char crypto-random URL-safe.
//   - The new app re-checks TBL_CIP_WEB_ACCESS on exchange, so access control
//     still applies to portal users.
// ============================================================================

using System.Data.SqlClient;   // CIPPortal is .NET Framework: System.Data.SqlClient
using System.Security.Cryptography;

namespace CIPPortal.Controllers.CIP
{
    public partial class CIP_LoginController : Controller
    {
        // Point this at wherever the new app is deployed (IIS site / port).
        private static readonly string CipAdminUrl =
            System.Configuration.ConfigurationManager.AppSettings["CipAdminUrl"] ?? "http://localhost:5000";

        /// <summary>Mint a one-time SSO ticket for the logged-in employee and
        /// redirect to the new CIP Admin app. Call ONLY after full login + OTP.</summary>
        private string MintSsoTicket(int empCode)
        {
            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            using (var con = new SqlConnection(
                System.Configuration.ConfigurationManager.ConnectionStrings["Default"].ConnectionString))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
                    INSERT INTO dbo.TBL_CIP_SSO_TICKET (TICKET, EMP_CODE, EXPIRES_ON)
                    VALUES (@t, @e, DATEADD(SECOND, 60, GETDATE()))", con))
                {
                    cmd.Parameters.AddWithValue("@t", ticket);
                    cmd.Parameters.AddWithValue("@e", empCode);
                    cmd.ExecuteNonQuery();
                }
            }
            return ticket;
        }

        /// <summary>Optional direct action: portal chrome link. Uses the session's EMP_ID.
        /// Redirects to the portal login when there is no session.</summary>
        public ActionResult GoToCipAdmin()
        {
            var empIdStr = Session["userid"] as string;
            if (string.IsNullOrEmpty(empIdStr))
                return RedirectToAction("Index");

            var ticket = MintSsoTicket(Convert.ToInt32(empIdStr));
            return Redirect($"{CipAdminUrl}/?sso={ticket}");
        }

        // ---- In Chk_Credential: after Session["UserSession"] = objEmployee; and the
        // ---- Google Authenticator Verify step passes, instead of the final
        // ---- RedirectToAction("StatusWiseDashboard", ...) add a query flag the user can click,
        // ---- or redirect straight through if you want portal-login to land in the new app:
        //
        //   var ticket = MintSsoTicket(objEmployee.Key);
        //   return Redirect($"{CipAdminUrl}/?sso={ticket}");
    }
}

// ============================================================================
// CIP_LogoutController — also end the new app's session on portal logout.
// The new app's JWT lives in the browser's localStorage, so the portal cannot
// clear it directly; simplest reliable approach is the redirect below, and the
// frontend calls DELETE /api/auth/logout automatically when it gets a 401
// (or you can point the logout link at /?sso=expired which fails cleanly to
// the new app's login page).
//
//   public ActionResult Index()
//   {
//       Session.Clear();
//       Session.Abandon();
//       return Redirect(System.Configuration.ConfigurationManager
//           .AppSettings["CipAdminUrl"] + "/login");   // new app shows its own login
//   }
// ============================================================================
