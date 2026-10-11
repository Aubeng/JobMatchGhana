using FluentAssertions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Enums;
using Xunit;

namespace JobMatch.Tests;

public class FsmTransitionTests
{
    private static ConversationSession NewSession() =>
        new() { WaId = "233200000000", State = ConversationState.New };

    private static NluResult Skill(string canonical, double conf) =>
        new("provide_skill", conf, new[] { new SkillCandidate(canonical, conf) });

    [Fact]
    public void Full_happy_path_reaches_active_and_captures_profile()
    {
        var s = NewSession();

        ConversationFsm.Step(s, "hi").Session.State.Should().Be(ConversationState.Consent);
        ConversationFsm.Step(s, "1").Session.State.Should().Be(ConversationState.Language);
        s.ConsentGiven.Should().BeTrue();

        ConversationFsm.Step(s, "1").Session.State.Should().Be(ConversationState.ProfileName);

        ConversationFsm.Step(s, "Kwame").Session.State.Should().Be(ConversationState.ProfileLocation);
        s.DisplayName.Should().Be("Kwame");

        ConversationFsm.Step(s, "Accra").Session.State.Should().Be(ConversationState.SkillsElicitation);
        s.LocationText.Should().Be("Accra");

        var extract = ConversationFsm.Step(s, "I can lay blocks and plaster", Skill("Masonry", 0.85));
        extract.Intent.Should().Be(Intent.ProvideSkill);
        s.PendingSkill!.CanonicalName.Should().Be("Masonry");

        var confirm = ConversationFsm.Step(s, "1");
        confirm.Intent.Should().Be(Intent.ConfirmSkill);
        confirm.NewlyConfirmedSkill!.CanonicalName.Should().Be("Masonry");
        confirm.NewlyConfirmedSkill.SourceUtterance.Should().Be("I can lay blocks and plaster");
        s.ConfirmedSkills.Should().Contain("Masonry");

        ConversationFsm.Step(s, "done").Session.State.Should().Be(ConversationState.Availability);
        ConversationFsm.Step(s, "1").Session.State.Should().Be(ConversationState.Active);
        s.Availability.Should().Be("Full-time");
    }

    [Fact]
    public void Consent_no_opts_out()
    {
        var s = NewSession();
        ConversationFsm.Step(s, "start"); // -> Consent
        var r = ConversationFsm.Step(s, "2");

        r.Intent.Should().Be(Intent.ConsentNo);
        r.OptedOut.Should().BeTrue();
        s.State.Should().Be(ConversationState.OptedOut);
    }

    [Fact]
    public void Stop_command_opts_out_from_any_state()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        s.ConsentGiven = true;

        var r = ConversationFsm.Step(s, "stop");

        r.Intent.Should().Be(Intent.Stop);
        r.OptedOut.Should().BeTrue();
        s.State.Should().Be(ConversationState.OptedOut);
    }

    [Fact]
    public void Help_command_does_not_change_state()
    {
        var s = NewSession();
        s.State = ConversationState.ProfileName;

        var r = ConversationFsm.Step(s, "help");

        r.Intent.Should().Be(Intent.Help);
        r.IsRepair.Should().BeFalse();
        s.State.Should().Be(ConversationState.ProfileName);
    }

    [Fact]
    public void Unrecognised_consent_input_is_a_logged_repair()
    {
        var s = NewSession();
        ConversationFsm.Step(s, "hello"); // -> Consent
        var r = ConversationFsm.Step(s, "maybe later");

        r.Intent.Should().Be(Intent.Repair);
        r.IsRepair.Should().BeTrue();
        s.State.Should().Be(ConversationState.Consent); // stays put
        s.RepairCount.Should().Be(1);
    }

    [Fact]
    public void Skill_refuse_clears_pending_for_retry()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        s.ConsentGiven = true;

        ConversationFsm.Step(s, "driving taxi", Skill("Driving", 0.8));
        s.PendingSkill.Should().NotBeNull();

        var r = ConversationFsm.Step(s, "2");
        r.Intent.Should().Be(Intent.RefineSkill);
        s.PendingSkill.Should().BeNull();
        s.ConfirmedSkills.Should().BeEmpty();
    }

    [Fact]
    public void No_skill_extracted_triggers_repair()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;

        var noneNlu = new NluResult("unknown", 0.2, Array.Empty<SkillCandidate>());
        var r = ConversationFsm.Step(s, "asdfghjkl", noneNlu);

        r.Intent.Should().Be(Intent.Repair);
        r.IsRepair.Should().BeTrue();
        r.NluConfidence.Should().Be(0.2);
        s.State.Should().Be(ConversationState.SkillsElicitation);
    }

    [Fact]
    public void Done_with_no_confirmed_skills_is_blocked()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;

        var r = ConversationFsm.Step(s, "done");

        r.IsRepair.Should().BeTrue();
        s.State.Should().Be(ConversationState.SkillsElicitation); // cannot advance without a skill
    }

    [Fact]
    public void Low_confidence_candidate_is_ignored()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;

        var weak = new NluResult("provide_skill", 0.3, new[] { new SkillCandidate("Masonry", 0.3) });
        var r = ConversationFsm.Step(s, "something", weak);

        r.Intent.Should().Be(Intent.Repair); // below MinSkillConfidence
        s.PendingSkill.Should().BeNull();
    }

    [Fact]
    public void RequiresNlu_only_for_fresh_skill_utterances()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;

        ConversationFsm.RequiresNlu(s, "I can cook").Should().BeTrue();
        ConversationFsm.RequiresNlu(s, "done").Should().BeFalse();
        ConversationFsm.RequiresNlu(s, "stop").Should().BeFalse();

        s.PendingSkill = new PendingSkill { CanonicalName = "Cooking" };
        ConversationFsm.RequiresNlu(s, "1").Should().BeFalse();  // confirming a pending candidate
        ConversationFsm.RequiresNlu(s, "no").Should().BeFalse();

        s.State = ConversationState.Consent;
        ConversationFsm.RequiresNlu(s, "yes").Should().BeFalse(); // NLU not used outside skills
    }

    [Fact]
    public void Trade_without_a_skill_probes_for_skills_in_that_trade()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        var nlu = new NluResult("TRADE_IDENTIFY", 0.94, Array.Empty<SkillCandidate>())
        {
            Trade = new TradeCandidate("woodwork", "Carpentry & Woodwork", 0.91,
                new[] { "Furniture making", "Roof carpentry", "Door & window frames" })
        };

        var r = ConversationFsm.Step(s, "am a carpenter", nlu);

        r.Intent.Should().Be(Intent.ProvideTrade);
        r.IsRepair.Should().BeFalse();             // progress, not a failed turn
        r.NluConfidence.Should().Be(0.91);
        r.Replies.Single().Text.Should().Contain("Carpentry & Woodwork").And.Contain("furniture making");
        s.State.Should().Be(ConversationState.SkillsElicitation);
        s.PendingSkill.Should().BeNull();
        s.RepairCount.Should().Be(0);
    }

    [Fact]
    public void A_specific_skill_wins_over_the_trade()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        var nlu = Skill("Roof carpentry", 0.9) with
        {
            Trade = new TradeCandidate("woodwork", "Carpentry & Woodwork", 0.9, Array.Empty<string>())
        };

        ConversationFsm.Step(s, "carpenter, i do roofing", nlu).Intent.Should().Be(Intent.ProvideSkill);
        s.PendingSkill!.CanonicalName.Should().Be("Roof carpentry");
    }

    [Fact]
    public void Every_skill_in_one_message_is_confirmed_in_turn()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        var nlu = new NluResult("SKILL_DECLARE", 0.97, new[]
        {
            new SkillCandidate("Furniture making", 0.88),
            new SkillCandidate("Roofing", 0.95),
            new SkillCandidate("Roofing", 0.60),        // duplicate mention
            new SkillCandidate("Tiling", 0.30)          // below MinSkillConfidence
        });
        const string utterance = "i do roofing and make furniture";

        ConversationFsm.Step(s, utterance, nlu).Replies.Single().Text.Should().Contain("*Roofing*");

        var r1 = ConversationFsm.Step(s, "yes");
        r1.NewlyConfirmedSkill!.CanonicalName.Should().Be("Roofing");
        r1.Replies.Single().Text.Should().Contain("You also said: *Furniture making*");

        var r2 = ConversationFsm.Step(s, "1");
        r2.NewlyConfirmedSkill!.CanonicalName.Should().Be("Furniture making");
        r2.NewlyConfirmedSkill.SourceUtterance.Should().Be(utterance);   // research data kept per skill
        r2.Replies.Single().Text.Should().Contain("Tell me another skill");

        s.ConfirmedSkills.Should().Equal("Roofing", "Furniture making");
        s.PendingSkill.Should().BeNull();
        s.QueuedSkills.Should().BeEmpty();
    }

    [Fact]
    public void Rejecting_a_candidate_moves_to_the_next_queued_one()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;
        var nlu = new NluResult("SKILL_DECLARE", 0.9, new[]
            { new SkillCandidate("Roofing", 0.9), new SkillCandidate("Roof carpentry", 0.8) });

        ConversationFsm.Step(s, "i do roofs", nlu);
        var r = ConversationFsm.Step(s, "no");

        r.Intent.Should().Be(Intent.RefineSkill);
        r.Replies.Single().Text.Should().Contain("*Roof carpentry*");
        s.PendingSkill!.CanonicalName.Should().Be("Roof carpentry");
        s.ConfirmedSkills.Should().BeEmpty();
    }

    [Fact]
    public void ShouldAnalyze_every_post_consent_turn_but_never_before_consent()
    {
        var s = NewSession();
        s.State = ConversationState.Consent;
        ConversationFsm.ShouldAnalyze(s, "yes i agree").Should().BeFalse();  // consent gate (DL-010)

        s.ConsentGiven = true;
        s.State = ConversationState.ProfileLocation;
        ConversationFsm.ShouldAnalyze(s, "i stay at madina").Should().BeTrue();
        ConversationFsm.ShouldAnalyze(s, "  ").Should().BeFalse();
        ConversationFsm.ShouldAnalyze(s, " 2 ").Should().BeFalse();             // menu digit

        s.State = ConversationState.OptedOut;
        ConversationFsm.ShouldAnalyze(s, "hello").Should().BeFalse();
    }

    [Fact]
    public void Whole_message_match_avoids_false_stop_command()
    {
        var s = NewSession();
        s.State = ConversationState.SkillsElicitation;

        // "I can stop leaks" must be treated as a skill utterance, not the stop command.
        var r = ConversationFsm.Step(s, "I can stop leaks", Skill("Plumbing", 0.8));
        r.Intent.Should().Be(Intent.ProvideSkill);
        s.State.Should().Be(ConversationState.SkillsElicitation);
    }
}
