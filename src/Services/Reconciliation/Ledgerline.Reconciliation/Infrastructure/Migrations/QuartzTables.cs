namespace Ledgerline.Reconciliation.Infrastructure.Migrations;

/// <summary>
/// The Quartz.NET ADO.NET job store tables (prefix <c>qrtz_</c>) for PostgreSQL. The script is embedded verbatim, header
/// comments and the leading DROP block included (harmless on the empty database the Initial migration starts from).
/// Source: https://github.com/quartznet/quartznet, tag v4.7.3 (commit f002ad0535fe809cf2c5402f2bc2899a28917daf),
/// path database/tables/tables_postgres.sql, sha256 2cfd61fd706512a310a1cd3758b2f029602d4551a2d64c78ee5a8fb45426ceb8.
/// No NuGet package ships this script in 4.7.3.
/// </summary>
public static class QuartzTables
{
    /// <summary>Every table the script creates, children before parents, as <c>Down</c> drops them.</summary>
    internal static readonly string[] DropOrder =
    [
        "qrtz_fired_triggers", "qrtz_paused_trigger_grps", "qrtz_paused_job_grps", "qrtz_scheduler_state", "qrtz_locks",
        "qrtz_simprop_triggers", "qrtz_simple_triggers", "qrtz_cron_triggers", "qrtz_blob_triggers", "qrtz_triggers",
        "qrtz_job_details", "qrtz_calendars", "qrtz_execution_history", "qrtz_misfire_history", "qrtz_job_status"
    ];

    /// <summary>The script text as embedded (see the project file).</summary>
    public static string Script { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(QuartzTables).Assembly.GetManifestResourceStream("Ledgerline.Reconciliation.QuartzTables.sql")
            ?? throw new InvalidOperationException("The embedded Quartz table script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
