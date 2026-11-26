using System.Reflection;

namespace Ledgerline.ArchitectureTests;

/// <summary>
/// Keeps docs/testing.md section 3 (F1 to F5) honest: every documented integration failure test
/// must exist, by exact name, in the test assembly that owns it. System tests are listed elsewhere.
/// </summary>
public class FailureTestInventoryTests
{
    private static readonly Dictionary<string, Assembly> Owners = new()
    {
        ["Messaging"] = typeof(Ledgerline.BuildingBlocks.Messaging.IntegrationTests.FailureScenarioTests).Assembly,
        ["Payments"] = typeof(Ledgerline.Payments.IntegrationTests.CrashTests).Assembly,
        ["Accounts"] = typeof(Ledgerline.Accounts.IntegrationTests.CrashTests).Assembly,
        ["Risk"] = typeof(Ledgerline.Risk.IntegrationTests.EvaluateRiskTests).Assembly,
    };

    private static readonly (string Owner, string Name)[] Documented =
    [
        ("Messaging", "Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery"),
        ("Payments", "Topups_accepted_during_broker_outage_complete_after_recovery"),
        ("Accounts", "Crash_after_ledger_commit_before_ack_does_not_double_post"),
        ("Payments", "Crash_after_saga_transition_commit_resumes_from_outbox"),
        ("Payments", "Crash_after_bank_capture_before_commit_does_not_capture_twice"),
        ("Messaging", "Crash_after_publish_before_marking_processed_republishes_and_consumer_deduplicates"),
        ("Messaging", "Same_message_delivered_twice_is_handled_once"),
        ("Messaging", "Same_message_delivered_concurrently_to_two_consumers_is_handled_once"),
        ("Accounts", "Same_operation_with_new_message_id_returns_same_reply_without_second_entry"),
        ("Accounts", "Duplicate_reserve_then_capture_commands_keep_hold_consistent"),
        ("Payments", "Duplicate_and_late_replies_do_not_move_a_final_payment"),
        ("Risk", "Evaluating_same_payment_twice_counts_once"),
        ("Accounts", "Concurrent_debits_never_overdraw"),
        ("Accounts", "Opposite_direction_transfers_do_not_deadlock"),
        ("Accounts", "Concurrent_postings_keep_stream_versions_gapless"),
        ("Payments", "Capture_and_void_race_has_exactly_one_winner"),
        ("Risk", "Concurrent_evaluations_respect_daily_limit"),
        ("Payments", "Same_idempotency_key_sent_concurrently_creates_one_payment"),
        ("Messaging", "Poison_message_goes_to_dead_queue_after_max_attempts"),
        ("Messaging", "Unknown_message_type_is_dead_lettered_immediately"),
        ("Payments", "Bank_timeout_with_success_is_resolved_by_lookup"),
        ("Payments", "Ledger_rejection_after_bank_capture_refunds_at_bank"),
        ("Payments", "Exhausted_compensation_ends_in_manual_review"),
        ("Payments", "Redis_down_idempotency_falls_back_to_database"),
        ("Risk", "Redis_down_velocity_falls_back_to_database"),
        ("Payments", "Authorization_expiry_releases_hold"),
        ("Accounts", "Projections_rebuilt_from_events_equal_live_projections"),
    ];

    [Fact]
    public void Every_documented_failure_test_exists()
    {
        var missing = Documented
            .Where(test => !Owners[test.Owner].GetTypes().Any(type => type.IsClass && HasTest(type, test.Name)))
            .Select(test => $"{test.Owner}.IntegrationTests: {test.Name}")
            .ToArray();

        missing.ShouldBeEmpty("documented failure tests missing from their owning assembly");
    }

    [Fact]
    public void Documented_failure_test_names_are_unique()
    {
        Documented.Select(test => test.Name).ShouldBeUnique();
    }

    private static bool HasTest(Type type, string name) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(method => method.Name == name && method.IsDefined(typeof(FactAttribute), inherit: true));
}
