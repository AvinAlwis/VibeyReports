using System;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// The one place a database password enters this process, and the one place it is taken back
    /// out of anything that could leave it.
    ///
    /// WHY AN ENVIRONMENT VARIABLE AND NOT A PLAN FIELD. <c>addTable</c> and
    /// <c>setTableLocation</c> make Crystal contact the database server to verify the object, and
    /// a report persists its connection's user name but never its password (measured; see
    /// docs/sdk-notes.md). So a password must come from somewhere. It must NOT come from the
    /// layout plan: plans are JSON files written to disk, quoted verbatim in documentation and
    /// pasted into bug reports, and <see cref="VibeyReports.Contracts.LayoutOperation"/>
    /// deliberately has no field that could carry a credential. The environment variable keeps the
    /// secret out of every artefact this tool writes.
    ///
    /// It is not a strong secret store, and the docs say so: any process running as the same user
    /// can read it, and making it permanent puts it in a shell profile or the user's registry
    /// environment. It is chosen for being strictly better than the plan, not for being good.
    ///
    /// READ AT THE POINT OF USE, never cached. The worker is a short-lived process today (one
    /// process per request), so caching would be indistinguishable -- which is exactly why it is
    /// not worth baking that assumption in. <see cref="Read"/> asks the environment every time.
    /// </summary>
    internal static class DatabasePassword
    {
        /// <summary>The environment variable the worker reads the password from.</summary>
        internal const string EnvVarName = "VIBEY_DB_PASSWORD";

        /// <summary>What the value is replaced with everywhere it might otherwise be printed.</summary>
        internal const string Redaction = "***";

        /// <summary>
        /// Reads the variable fresh from the environment. Returns null when it is unset or empty;
        /// callers must not distinguish those two, because an empty string is not a usable password
        /// and reporting "set but empty" would be a hint about a secret's contents.
        /// </summary>
        internal static string Read()
        {
            var value = Environment.GetEnvironmentVariable(EnvVarName);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// The password, or an <see cref="InvalidOperationException"/> that names the variable and
        /// explains what it is for. This is an APPLIER-time error on purpose:
        /// <see cref="VibeyReports.Contracts.LayoutPlanValidator"/> is pure -- no I/O, no
        /// environment access -- and stays that way.
        /// </summary>
        internal static string Require(string action)
        {
            var value = Read();
            if (value == null) throw new InvalidOperationException(MissingMessage(action));
            return value;
        }

        internal static string MissingMessage(string action)
        {
            return
                $"\"{action}\" needs a database password, and the environment variable {EnvVarName} is not set. " +
                "Crystal contacts the database server to verify the table before this operation can complete, " +
                "and a report stores its connection's user name but never its password, so the password has to " +
                $"be supplied out of band. Set {EnvVarName} to the password of the database user the report's " +
                "saved connection logs on as, in the environment of the process that launches this tool, then " +
                "retry. It is deliberately NOT a field of the layout plan -- plans are JSON files written to " +
                "disk and must never record a credential -- and it is never included in any response, error " +
                "message or saved report. Every other operation, removeTable included, needs no password at all.";
        }

        /// <summary>
        /// Removes the secret from text that is about to be thrown, logged or serialised. Crystal's
        /// own COM messages are the risk this exists for: the message text of a rejected logon is
        /// composed by the database driver, which we do not control, and it travels into
        /// WorkerResponse.Error and (via ex.ToString()) into the worker's stderr.
        ///
        /// Ordinal, case-sensitive, and every occurrence -- a password is matched byte for byte, so
        /// a case-insensitive match would only redact MORE than it must, and would be a (tiny)
        /// oracle about the value. A null/empty secret scrubs nothing.
        /// </summary>
        internal static string Scrub(string text, string secret)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
            return text.Replace(secret, Redaction);
        }

        /// <summary>
        /// Scrubs against whatever the variable currently holds. Used on paths where the value was
        /// not read into a local -- notably the outermost wrapping in the worker's entry point.
        /// </summary>
        internal static string Scrub(string text)
        {
            return Scrub(text, Read());
        }
    }
}
