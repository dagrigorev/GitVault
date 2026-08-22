using System.Text;

namespace GitVault.Core.Profiles;

/// <summary>
/// Renders a set of planned changes as the preview the user has to approve.
/// </summary>
/// <remarks>
/// Shared by every kind of plan rather than reimplemented per plan type. The dry-run preview is
/// the thing standing between a user and a write they did not intend; having two renderers means
/// one of them can drift and start showing something other than what will run.
///
/// Values appear verbatim. Nothing that reaches a plan is a secret — profiles hold references,
/// and configuration values are the very thing the user is deciding about — so there is nothing
/// here to redact. A plan carrying a secret would be a bug in whatever built it, not something
/// this renderer should paper over.
/// </remarks>
public static class PlanDiff
{
    /// <summary>Renders changes and blockers as unified-diff-like text.</summary>
    /// <param name="changes">Changes in execution order.</param>
    /// <param name="blockers">Reasons the plan cannot be applied.</param>
    /// <returns>The preview text.</returns>
    public static string Render(IReadOnlyList<PlannedChange> changes, IReadOnlyList<string> blockers)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(blockers);

        var builder = new StringBuilder();

        foreach (var change in changes)
        {
            builder.Append(change.Kind switch
            {
                ChangeKind.GitConfigSet => "git config ",
                ChangeKind.GitConfigUnset => "git config --unset ",
                ChangeKind.SshConfigBlock => "ssh config block ",
                ChangeKind.SshConfigBlockRemoval => "ssh config block removal ",
                ChangeKind.FileWrite => "write ",
                ChangeKind.FileDelete => "delete ",
                _ => "agent ",
            });

            builder.Append(change.Target).Append('\n');

            if (change.IsNoOp)
            {
                builder.Append("  (no change)\n");
                continue;
            }

            // A whole file is shown as a difference rather than twice over. Dumping every line as
            // a removal and then again as an addition is technically complete and practically
            // unreadable, and a preview nobody reads is not a safety mechanism.
            if (change.Kind is ChangeKind.FileWrite or ChangeKind.FileDelete)
            {
                builder.Append(TextDiff.RenderText(change.Before, change.After));
                continue;
            }

            foreach (var line in (change.Before ?? string.Empty).Split('\n'))
            {
                if (change.Before is not null)
                {
                    builder.Append("  - ").Append(line).Append('\n');
                }
            }

            foreach (var line in (change.After ?? string.Empty).Split('\n'))
            {
                if (change.After is not null)
                {
                    builder.Append("  + ").Append(line).Append('\n');
                }
            }
        }

        builder.Append(RenderBlockers(blockers));

        return builder.ToString();
    }

    /// <summary>Renders the reasons a plan cannot be applied.</summary>
    /// <param name="blockers">Reasons, as identifiers the view turns into text.</param>
    /// <returns>The lines, or an empty string when there are none.</returns>
    public static string RenderBlockers(IReadOnlyList<string> blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);

        if (blockers.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("\n");

        foreach (var blocker in blockers)
        {
            builder.Append("! ").Append(blocker).Append('\n');
        }

        return builder.ToString();
    }
}
