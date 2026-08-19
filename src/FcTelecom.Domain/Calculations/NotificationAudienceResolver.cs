using FcTelecom.Domain.Notifications;

namespace FcTelecom.Domain.Calculations;

/// <summary>
/// Everything the resolver needs to know about the specific event being evaluated.
/// </summary>
/// <param name="ThresholdDays">
/// Days remaining, for threshold-driven events. Null for immediate events such as an
/// outage, where escalation steps do not apply.
/// </param>
/// <param name="DeadlineConfirmed">
/// Whether a person has confirmed the notice deadline. Null when the concept does not
/// apply to this event type.
/// </param>
/// <param name="ActionRecorded">Whether anyone has recorded a decision or action yet.</param>
/// <param name="RecordOwnerEmail">The contract or record owner's address, if there is one.</param>
/// <param name="RoleMemberEmails">Role name to the addresses of everyone holding it.</param>
public readonly record struct NotificationContext(
    int? ThresholdDays,
    bool? DeadlineConfirmed,
    bool? ActionRecorded,
    string? RecordOwnerEmail,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RoleMemberEmails);

/// <summary>
/// Exactly who this rule would reach, and why — the preview shown before a rule is enabled.
/// </summary>
public readonly record struct NotificationAudience(
    IReadOnlyList<string> EmailRecipients,
    IReadOnlyList<string> EscalationEmailRecipients,
    string? TeamsChannel,
    string? WebhookUrl,
    IReadOnlyList<string> Explanations,
    IReadOnlyList<string> Warnings,
    bool WouldSend)
{
    public int TotalRecipientCount => EmailRecipients.Count + EscalationEmailRecipients.Count;
}

/// <summary>
/// Works out who a notification rule would actually reach for a given event.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a rule can be previewed before it is switched on. The single most common
/// notification failure is not a bug in delivery — it is a rule that reaches nobody, or
/// reaches four hundred people, and nobody found out until it fired. A preview turns that
/// into a five-second check.
/// </para>
/// <para>
/// Pure function: no clock, no database, no mail client. Every input is passed in, which is
/// what makes the awkward cases — an unconfirmed deadline at the escalation threshold, a
/// role with no members, a rule with no recipients at all — testable rather than hoped for.
/// </para>
/// </remarks>
public static class NotificationAudienceResolver
{
    public static NotificationAudience Resolve(NotificationRule rule, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var primary = new List<string>();
        var escalation = new List<string>();
        var why = new List<string>();
        var warnings = new List<string>();

        // ── Primary audience ────────────────────────────────────────────────────────
        if (rule.NotifyRecordOwner)
        {
            if (!string.IsNullOrWhiteSpace(context.RecordOwnerEmail))
            {
                primary.Add(context.RecordOwnerEmail);
                why.Add($"{context.RecordOwnerEmail} — record owner.");
            }
            else
            {
                warnings.Add(
                    "The rule notifies the record owner, but this record has no owner assigned. " +
                    "Nobody will be told on that basis.");
            }
        }

        foreach (string address in Split(rule.Recipients))
        {
            primary.Add(address);
            why.Add($"{address} — named recipient on the rule.");
        }

        if (!string.IsNullOrWhiteSpace(rule.SharedMailbox))
        {
            primary.Add(rule.SharedMailbox);
            why.Add($"{rule.SharedMailbox} — shared team mailbox.");
        }

        AddRole(rule.RoleScope, context, primary, why, warnings, "rule role scope");

        // ── Escalation ──────────────────────────────────────────────────────────────
        if (context.ThresholdDays is { } daysRemaining)
        {
            foreach (NotificationEscalationStep step in rule.EscalationSteps
                         .Where(step => daysRemaining <= step.ThresholdDays)
                         .OrderByDescending(step => step.ThresholdDays))
            {
                if (!ConditionMet(step.Condition, context, out string conditionNote))
                {
                    why.Add($"Escalation at {step.ThresholdDays} days did not fire — {conditionNote}");
                    continue;
                }

                var before = escalation.Count;

                foreach (string address in Split(step.Recipients))
                {
                    escalation.Add(address);
                }

                AddRole(step.RoleScope, context, escalation, why, warnings,
                        $"escalation role scope at {step.ThresholdDays} days");

                if (escalation.Count > before)
                {
                    why.Add($"Escalation at {step.ThresholdDays} days fired ({conditionNote}): " +
                            $"{string.Join(", ", escalation.Skip(before))}.");
                }
                else
                {
                    warnings.Add(
                        $"The escalation step at {step.ThresholdDays} days fired but resolves to " +
                        "nobody. Check its recipients and role scope.");
                }
            }
        }

        var dedupedPrimary = Dedupe(primary);
        // Somebody already on the primary list does not need a second copy from escalation.
        var dedupedEscalation = Dedupe(escalation)
            .Where(address => !dedupedPrimary.Contains(address, StringComparer.OrdinalIgnoreCase))
            .ToList();

        string? teams = rule.Channels.HasFlag(NotificationChannel.Teams)
            ? rule.TeamsChannelReference
            : null;

        string? webhook = rule.Channels.HasFlag(NotificationChannel.Webhook)
            ? rule.WebhookUrl
            : null;

        if (rule.Channels.HasFlag(NotificationChannel.Teams) && string.IsNullOrWhiteSpace(teams))
        {
            warnings.Add("The Teams channel is selected but no channel reference is configured.");
        }

        if (rule.Channels.HasFlag(NotificationChannel.Webhook) && string.IsNullOrWhiteSpace(webhook))
        {
            warnings.Add("The webhook channel is selected but no URL is configured.");
        }

        bool anyEmail = rule.Channels.HasFlag(NotificationChannel.Email) &&
                        (dedupedPrimary.Count > 0 || dedupedEscalation.Count > 0);

        bool wouldSend = rule.Enabled && (anyEmail || teams is not null || webhook is not null);

        if (!rule.Enabled)
        {
            why.Add("The rule is disabled, so nothing would be sent.");
        }
        else if (!wouldSend)
        {
            warnings.Add(
                "This rule is enabled but would reach nobody. An enabled rule with no " +
                "recipients looks like it is working and is not.");
        }

        if (rule.Channels.HasFlag(NotificationChannel.Email) &&
            dedupedPrimary.Count + dedupedEscalation.Count > 25)
        {
            warnings.Add(
                $"This rule would email {dedupedPrimary.Count + dedupedEscalation.Count} people. " +
                "Consider a shared mailbox or a Teams channel instead of a large recipient list.");
        }

        return new NotificationAudience(
            dedupedPrimary, dedupedEscalation, teams, webhook, why, warnings, wouldSend);
    }

    private static bool ConditionMet(
        EscalationCondition condition, NotificationContext context, out string note)
    {
        bool unconfirmed = context.DeadlineConfirmed == false;
        bool noAction = context.ActionRecorded == false;

        switch (condition)
        {
            case EscalationCondition.Always:
                note = "unconditional";
                return true;

            case EscalationCondition.IfDeadlineUnconfirmed:
                note = unconfirmed ? "the deadline is still unconfirmed" : "the deadline has been confirmed";
                return unconfirmed;

            case EscalationCondition.IfNoActionRecorded:
                note = noAction ? "no action has been recorded" : "an action has been recorded";
                return noAction;

            case EscalationCondition.IfUnconfirmedOrNoAction:
                note = (unconfirmed, noAction) switch
                {
                    (true, true) => "the deadline is unconfirmed and no action has been recorded",
                    (true, false) => "the deadline is still unconfirmed",
                    (false, true) => "no action has been recorded",
                    _ => "the deadline is confirmed and an action has been recorded",
                };
                return unconfirmed || noAction;

            default:
                note = "unrecognised condition";
                return false;
        }
    }

    private static void AddRole(
        string? role, NotificationContext context, List<string> into,
        List<string> why, List<string> warnings, string source)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        if (context.RoleMemberEmails is not null &&
            context.RoleMemberEmails.TryGetValue(role, out IReadOnlyList<string>? members) &&
            members.Count > 0)
        {
            into.AddRange(members);
            why.Add($"{members.Count} member(s) of the {role} role — {source}.");
            return;
        }

        warnings.Add($"The {source} names the {role} role, but nobody currently holds it.");
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<string> Dedupe(IEnumerable<string> addresses) =>
        [.. addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)];
}
