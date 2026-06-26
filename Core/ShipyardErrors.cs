using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Octokit;

namespace ShipyardPlugin
{
    // Maps low-level Octokit / network exceptions to short, actionable operator-facing text, so the UI
    // shows guidance ("sign in again", "check the repo name") instead of a raw 'NotFoundException: Not Found'.
    internal static class ShipyardErrors
    {
        // A 401 means the token is dead (expired/revoked); wipe it so the next thing the user sees is the
        // sign-in button, not this error on every action. Kept SEPARATE from Explain() so the destructive
        // side effect is explicit at the call site (Explain is a pure formatter). Repo config + Client ID kept.
        public static void WipeIfExpired(Exception ex)
        {
            if (Unwrap(ex) is AuthorizationException)
            {
                try { Auth.SignOut(); }
                catch (Exception sx) { Plugin.Log("WipeIfExpired: SignOut failed: " + sx.Message); }
            }
        }

        public static string Explain(Exception ex)
        {
            ex = Unwrap(ex);
            string repo = (Auth.RepoOwner ?? "") + "/" + (Auth.RepoName ?? "");

            // RateLimitExceededException derives from ForbiddenException, so test it first.
            if (ex is RateLimitExceededException)
                return "GitHub rate limit reached. Wait a few minutes and try again.";

            if (ex is AuthorizationException)
                // The caller is expected to call WipeIfExpired(ex) for the actual token wipe.
                return "Your GitHub sign-in was invalid or expired - it has been WIPED.\n" +
                       "Open the Shipyard (or Account) and sign in again.\n\n" +
                       "(Using your own GitHub App? Make sure 'Expire user authorization\n" +
                       "tokens' is UNCHECKED on the app, or this happens every ~8 hours.)";

            if (ex is NotFoundException)
                return "Not found, or your account can't see it:\n    " + repo + "\n" +
                       "- Check Owner + Repo name in Account.\n" +
                       "- If the repo is brand-new/empty, run Account -> Initialize repo.\n" +
                       "- If you were just added, Account -> Accept repo invitation.";

            if (ex is ForbiddenException)
                return "GitHub refused that request (403).\n" +
                       "You may lack permission on " + repo + ", or hit a temporary limit. Check your access, then retry.";

            if (ex is ApiValidationException ave)
                return "GitHub rejected the request:\n" + FirstError(ave);

            if (ex is HttpRequestException || ex is WebException || ex is SocketException ||
                ex is TaskCanceledException || ex is OperationCanceledException)
                return "Couldn't reach GitHub. Check your internet connection and try again.";

            if (ex is ApiException api)
                return "GitHub API error:\n" + (string.IsNullOrEmpty(api.Message) ? api.GetType().Name : api.Message);

            return string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static Exception Unwrap(Exception ex)
        {
            // Peel known single-wrapper exceptions so a nested Octokit/network exception still matches the
            // typed branches in Explain. For AggregateException use GetBaseException (handles the multi-inner
            // case), and unwrap reflection's TargetInvocationException when it carries an inner exception.
            while (true)
            {
                if (ex is AggregateException ag)
                {
                    var baseEx = ag.GetBaseException();
                    if (baseEx == null || ReferenceEquals(baseEx, ex)) return ex;
                    ex = baseEx;
                    continue;
                }
                if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                {
                    ex = tie.InnerException;
                    continue;
                }
                return ex;
            }
        }

        private static string FirstError(ApiValidationException ave)
        {
            try
            {
                var e = ave.ApiError;
                if (e != null)
                {
                    if (e.Errors != null && e.Errors.Count > 0)
                    {
                        var d = e.Errors[0];
                        if (!string.IsNullOrEmpty(d.Message)) return d.Message;
                        return (d.Field + " " + d.Code).Trim();
                    }
                    if (!string.IsNullOrEmpty(e.Message)) return e.Message;
                }
            }
            // This is itself a best-effort error-message formatter: if digging into the validation payload
            // throws, just fall back to the raw exception message (logging here would be circular noise).
            catch { }
            return ave.Message;
        }
    }
}
