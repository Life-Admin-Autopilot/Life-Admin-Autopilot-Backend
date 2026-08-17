using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// How long a matter takes to do — the duration ladder behind Phase 2's
/// window-aware scheduling.
///
/// <para>
/// The precedence is the whole design: what the matter itself says beats what its
/// title suggests, which beats what its domain suggests. Every case here pins one
/// rung of that, because a resolver that silently prefers the wrong rung produces a
/// schedule that looks entirely reasonable and is wrong.
/// </para>
/// </summary>
public sealed class ReminderDurationTests
{
    // ---- a stored estimate wins ------------------------------------------------

    [Theory]
    [InlineData("user")]
    [InlineData("ai")]
    public void prefers_the_matters_own_estimate_over_any_table(string source)
    {
        // A stored estimate is a judgement about THIS matter; the tables are
        // judgements about matters shaped like it, which is less information. That
        // holds for an AI estimate too, not only a hand-typed one.
        var estimate = new TaskEstimateDocument { MinMinutes = 15, MaxMinutes = 90, Source = source };

        // The title would otherwise resolve to 10 minutes.
        Assert.Equal(90, ReminderDuration.ResolveMinutes("Pay electricity bill", "home", estimate));
    }

    [Fact]
    public void reserves_the_upper_bound_of_the_range_not_the_lower()
    {
        // The question this answers is "when is it too late to start", and only the
        // generous end of a range is safe to answer it with — the same reasoning
        // EstimateNormalizer already applies when it rounds ties up.
        var estimate = new TaskEstimateDocument { MinMinutes = 5, MaxMinutes = 120, Source = "ai" };

        Assert.Equal(120, ReminderDuration.ResolveMinutes("Sort the thing out", "home", estimate));
    }

    [Fact]
    public void ignores_an_estimate_that_carries_no_usable_upper_bound()
    {
        // Rows written before the field existed, and anything that got through with
        // a zero, must fall through to the tables rather than reserving no time.
        var empty = new TaskEstimateDocument { MinMinutes = 0, MaxMinutes = 0, Source = "ai" };

        Assert.Equal(45, ReminderDuration.ResolveMinutes("Sort the thing out", "home", empty));
    }

    // ---- the keyword table -----------------------------------------------------

    [Theory]
    [InlineData("Renew passport", 90)]
    [InlineData("Renew driving licence", 60)]
    [InlineData("Car registration", 60)]
    [InlineData("Car insurance", 45)]
    [InlineData("File tax return", 120)]
    [InlineData("Cancel Netflix subscription", 15)]
    [InlineData("Pay electricity bill", 10)]
    [InlineData("Dentist appointment", 60)]
    public void reads_a_realistic_duration_off_the_title(string title, int expected)
    {
        Assert.Equal(expected, ReminderDuration.ResolveMinutes(title, "home", estimate: null));
    }

    [Fact]
    public void scores_duration_and_warning_independently_of_each_other()
    {
        // The two tables answer different questions about the same eight rows, and
        // this is the case that proves they are not secretly the same number: a bill
        // earns the SECOND-shortest duration and a generous five days of warning,
        // because paying is quick and REMEMBERING is the hard part.
        var bill = new ReminderTaskShape("Pay electricity bill", "home", "reminder", null);

        Assert.Equal(10, ReminderDuration.ResolveMinutes("Pay electricity bill", "home", null));
        Assert.Equal(5, ReminderLeadTime.ComputeLeadDays(bill));

        // A dentist appointment is the mirror image: six times the duration, a fifth
        // of the warning.
        var appointment = new ReminderTaskShape("Dentist appointment", "health", "reminder", null);

        Assert.Equal(60, ReminderDuration.ResolveMinutes("Dentist appointment", "health", null));
        Assert.Equal(1, ReminderLeadTime.ComputeLeadDays(appointment));
    }

    // ---- the domain fallback ---------------------------------------------------

    [Theory]
    [InlineData("health", 60)]
    [InlineData("home", 45)]
    [InlineData("car", 60)]
    [InlineData("finance", 15)]
    [InlineData("family", 45)]
    [InlineData("pets", 30)]
    public void falls_back_to_the_domain_when_the_title_says_nothing(string domain, int expected)
    {
        Assert.Equal(expected, ReminderDuration.ResolveMinutes("Sort the thing out", domain, null));
    }

    [Fact]
    public void falls_back_again_for_a_domain_outside_the_vocabulary()
    {
        Assert.Equal(
            ReminderDuration.FallbackMinutes,
            ReminderDuration.ResolveMinutes("Sort the thing out", "not-a-domain", null));
    }

    [Fact]
    public void lets_a_keyword_beat_the_domain_default()
    {
        // 'finance' defaults to 15 minutes; a tax return in that domain is still 120.
        Assert.Equal(120, ReminderDuration.ResolveMinutes("File tax return", "finance", null));
    }

    // ---- the ladder ------------------------------------------------------------

    [Fact]
    public void keeps_every_derived_value_on_the_bucket_ladder()
    {
        // A derived duration has to be indistinguishable in kind from a stored one.
        // The moment one is not a bucket, the UI is rendering "37 minutes" beside
        // "45 minutes" and claiming a precision the system does not have.
        var titles = new[]
        {
            "Renew passport", "Renew driving licence", "Car registration", "Car insurance",
            "File tax return", "Cancel Netflix subscription", "Pay electricity bill",
            "Dentist appointment", "Sort the thing out",
        };
        var domains = new[] { "health", "home", "car", "finance", "family", "pets", "not-a-domain" };

        foreach (var title in titles)
        {
            foreach (var domain in domains)
            {
                var minutes = ReminderDuration.ResolveMinutes(title, domain, null);
                Assert.Contains(minutes, EstimateNormalizer.Buckets);
            }
        }
    }

    [Fact]
    public void never_reserves_longer_than_the_ladders_ceiling()
    {
        // The window is subtracted from a deadline, so an unbounded duration would
        // schedule a nudge arbitrarily far in the past.
        var huge = new TaskEstimateDocument { MinMinutes = 5, MaxMinutes = 240, Source = "user" };

        Assert.Equal(240, ReminderDuration.ResolveMinutes("Sort the thing out", "home", huge));
    }

    // ---- the whole-matter overload ---------------------------------------------

    [Fact]
    public void reads_the_same_answer_off_a_whole_matter()
    {
        var task = new TaskDocument { Title = "File tax return", Domain = "finance" };

        Assert.Equal(120, ReminderDuration.ResolveMinutes(task));
    }
}
