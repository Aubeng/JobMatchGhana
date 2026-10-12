using FluentAssertions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Enums;
using Xunit;

namespace JobMatch.Tests;

/// <summary>
/// Transition tests for the S&D hierarchical state machine (Design §6.2, Figure 3). Each test
/// names the state, rule or requirement it checks.
/// </summary>
public class FsmTransitionTests
{
    private static readonly DialogueCatalog Catalog = TestCatalog.Instance;

    private static ConversationSession NewSession() => new() { WaId = "233200000000" };

    private static FsmStepResult Step(ConversationSession s, string text, NluResult? nlu = null)
        => ConversationFsm.Step(s, text, nlu, Catalog);

    /// <summary>A session that has consented and is at the given state with earlier slots filled.</summary>
    private static ConversationSession At(ConversationState state)
    {
        var s = NewSession();
        s.ConsentGiven = true;
        if (state >= ConversationState.ElicitSkills) { s.TradeId = "construction"; s.TradeName = "Construction & Building"; }
        if (state >= ConversationState.ElicitExperience)
            s.Skills.Add(new CapturedSkill { CanonicalName = "Block laying", SourceUtterance = "list:1", Confidence = 1 });
        if (state >= ConversationState.ElicitLocation) { s.ExperienceYears = 3; s.ExperienceText = "1 to 5 years"; s.Availability = "Full-time"; }
        if (state >= ConversationState.ConfirmProfile)
        {
            s.LocationDistrict = "La Nkwantanang Madina"; s.LocationRegion = "Greater Accra"; s.LocationText = "Madina";
        }
        s.State = state;
        return s;
    }

    private static NluResult Nlu(string intent, double conf = 0.95, params SkillCandidate[] skills)
        => new(intent, conf, skills) { ModelVersion = "test" };

    // ---- Happy path ------------------------------------------------------------------------

    [Fact]
    public void Happy_path_follows_the_SD_states_in_order_with_taps_only()
    {
        var s = NewSession();
        var states = new List<ConversationState>();
        void Tap(string id) { Step(s, id); states.Add(s.State); }

        var welcome = Step(s, "hi");
        welcome.Replies.Single().Buttons.Should().HaveCount(2);
        states.Add(s.State);
        Tap("1");              // consent
        Tap("1");              // trade list: Construction & Building
        Tap("1,3");            // skills: Block laying, Concrete work
        Tap("done");
        Tap("exp_1to5");       // experience button
        Tap("1");              // Full-time
        Tap("2");              // area list: Madina
        var confirm = Step(s, "1");
        states.Add(s.State);

        states.Should().Equal(
            ConversationState.Welcome, ConversationState.ElicitTrade, ConversationState.ElicitSkills,
            ConversationState.ElicitSkills, ConversationState.ElicitExperience, ConversationState.ElicitExperience,
            ConversationState.ElicitLocation, ConversationState.ConfirmProfile, ConversationState.Active);
        states.Should().HaveCountLessOrEqualTo(12, "S&D target: a complete profile in <= 12 turns");

        confirm.ProfileConfirmed.Should().BeTrue();
        confirm.Intent.Should().Be(Intent.ConfirmProfile);
        s.ConsentGiven.Should().BeTrue();
        s.TradeId.Should().Be("construction");
        s.Skills.Select(k => k.CanonicalName).Should().Equal("Block laying", "Concrete work");
        s.Skills.Should().OnlyContain(k => k.SourceUtterance.StartsWith("list:") && k.Confidence == 1.0);
        s.ExperienceYears.Should().Be(3);
        s.Availability.Should().Be("Full-time");
        s.LocationDistrict.Should().Be("La Nkwantanang Madina");
        s.ProfileConfirmed.Should().BeTrue();
    }

    [Fact]
    public void S5_summary_shows_every_slot_before_confirmation_FR10()
    {
        var s = At(ConversationState.ElicitLocation);
        var r = Step(s, "Madina");

        s.State.Should().Be(ConversationState.ConfirmProfile);
        var text = r.Replies.Single().Text;
        text.Should().Contain("Construction & Building").And.Contain("Block laying").And.Contain("1 to 5 years")
            .And.Contain("Full-time").And.Contain("Madina (La Nkwantanang Madina)");
        r.ProfileConfirmed.Should().BeFalse("nothing is final until the worker confirms");
    }

    // ---- S0 --------------------------------------------------------------------------------

    [Fact]
    public void S0_any_first_message_starts_with_consent_FR01_FR02()
    {
        var s = NewSession();
        var r = Step(s, "help");   // even a command
        s.State.Should().Be(ConversationState.Welcome);
        r.Replies.Single().Text.Should().Contain("consent").And.Contain("delete").And.Contain("stop");
    }

    [Fact]
    public void S0_consent_refused_opts_out_without_consent()
    {
        var s = NewSession();
        Step(s, "hi");
        var r = Step(s, "2");
        r.OptedOut.Should().BeTrue();
        r.Intent.Should().Be(Intent.ConsentNo);
        s.ConsentGiven.Should().BeFalse();
        s.State.Should().Be(ConversationState.OptedOut);
    }

    // ---- S1 --------------------------------------------------------------------------------

    [Fact]
    public void S1_trade_from_free_text_at_or_above_threshold_goes_to_skills()
    {
        var s = At(ConversationState.ElicitTrade);
        var nlu = Nlu("TRADE_IDENTIFY") with { Trade = new TradeCandidate("woodwork", "Carpentry & Woodwork", 0.89, Array.Empty<string>()) };

        var r = Step(s, "am a carpentar", nlu);

        s.TradeId.Should().Be("woodwork");
        s.State.Should().Be(ConversationState.ElicitSkills);
        r.Replies.Single().List.Should().HaveCount(10, "the trade's skills are offered as a list (S2)");
    }

    [Fact]
    public void S1_low_confidence_trade_is_put_back_as_a_question()
    {
        var s = At(ConversationState.ElicitTrade);
        var nlu = Nlu("TRADE_IDENTIFY") with { Trade = new TradeCandidate("woodwork", "Carpentry & Woodwork", 0.6, Array.Empty<string>()) };

        var r = Step(s, "i do wood", nlu);
        r.Intent.Should().Be(Intent.Clarify);
        r.IsRepair.Should().BeFalse();
        s.TradeId.Should().BeNull();

        Step(s, "yes");
        s.TradeId.Should().Be("woodwork");
        s.State.Should().Be(ConversationState.ElicitSkills);
    }

    [Fact]
    public void Mixed_initiative_fills_slots_volunteered_early_and_skips_their_questions()
    {
        var s = At(ConversationState.ElicitTrade);
        var nlu = Nlu("SKILL_DECLARE", 0.95, new SkillCandidate("Block laying", 0.97)) with
        {
            Trade = new TradeCandidate("construction", "Construction & Building", 0.97, Array.Empty<string>(), "trade"),
            LocationDistrict = "La Nkwantanang Madina", LocationRegion = "Greater Accra", LocationText = "madina", LocationConfidence = 0.96,
            ExperienceYears = 5, ExperienceText = "5 years"
        };

        Step(s, "am a mason, i lay blocks in madina for 5 years", nlu);
        s.State.Should().Be(ConversationState.ElicitSkills, "skills given: offer more or done");
        s.Skills.Select(k => k.CanonicalName).Should().Equal("Block laying");

        Step(s, "done");
        s.State.Should().Be(ConversationState.ElicitExperience);
        s.ExperienceYears.Should().Be(5, "experience was already given...");
        var r = Step(s, "2");                     // ...so S3 asks only availability
        s.State.Should().Be(ConversationState.ConfirmProfile, "location was already given too");
        r.Replies.Single().Text.Should().Contain("Madina");
    }

    // ---- S2 --------------------------------------------------------------------------------

    [Fact]
    public void S2_confident_skills_are_added_and_uncertain_ones_confirmed_first()
    {
        var s = At(ConversationState.ElicitSkills);
        var nlu = Nlu("SKILL_DECLARE", 0.95, new SkillCandidate("Plastering", 0.92), new SkillCandidate("Tiling", 0.6),
            new SkillCandidate("Roofing", 0.3));

        var r = Step(s, "i plaster and maybe tiles", nlu);
        s.Skills.Select(k => k.CanonicalName).Should().Equal("Plastering");
        r.Replies.Single().Text.Should().Contain("*Tiling*");

        Step(s, "yes");
        s.Skills.Select(k => k.CanonicalName).Should().Equal("Plastering", "Tiling");
        s.Skills.Should().OnlyContain(k => k.SourceUtterance == "i plaster and maybe tiles", "provenance is kept");
    }

    [Fact]
    public void S2_every_skill_in_one_message_is_kept()
    {
        var s = At(ConversationState.ElicitSkills);
        var nlu = Nlu("SKILL_DECLARE", 0.97, new SkillCandidate("Roofing", 0.95), new SkillCandidate("Plastering", 0.9));
        Step(s, "i do roofing and plastering", nlu);
        s.Skills.Select(k => k.CanonicalName).Should().BeEquivalentTo("Roofing", "Plastering");
    }

    [Fact]
    public void S2_number_list_picks_skills_and_done_needs_at_least_one()
    {
        var s = At(ConversationState.ElicitSkills);
        Step(s, "done").IsRepair.Should().BeTrue();

        Step(s, "2 and 4");
        s.Skills.Select(k => k.CanonicalName).Should().Equal("Plastering", "Tiling");
        Step(s, "done");
        s.State.Should().Be(ConversationState.ElicitExperience);
    }

    [Fact]
    public void S2_list_offers_done_once_a_skill_is_chosen_and_hides_chosen_skills()
    {
        var s = At(ConversationState.ElicitSkills);
        var r = Step(s, "1");
        var rows = r.Replies.Single().List!;
        rows.Should().HaveCount(10);
        rows[0].Id.Should().Be(DialoguePrompts.Done);
        rows.Should().NotContain(o => o.Title == "Block laying");
    }

    // ---- S3 --------------------------------------------------------------------------------

    [Fact]
    public void S3_years_by_button_typed_number_or_nlu_then_availability()
    {
        var a = At(ConversationState.ElicitExperience); a.ExperienceYears = null; a.Availability = null;
        Step(a, "exp_gt5");
        a.ExperienceYears.Should().Be(6);
        a.ExperienceText.Should().Be("More than 5 years");

        var b = At(ConversationState.ElicitExperience); b.ExperienceYears = null; b.Availability = null;
        Step(b, "7");
        b.ExperienceYears.Should().Be(7);

        var c = At(ConversationState.ElicitExperience); c.ExperienceYears = null; c.Availability = null;
        Step(c, "since 2018", Nlu("SKILL_DECLARE") with { ExperienceYears = 8, ExperienceText = "since 2018" });
        c.ExperienceYears.Should().Be(8);
        c.State.Should().Be(ConversationState.ElicitExperience, "availability is the sub-machine's second slot");

        Step(c, "mornings only", Nlu("AVAILABILITY_STATE", 0.93));
        c.Availability.Should().Be("mornings only");
        c.State.Should().Be(ConversationState.ElicitLocation);
    }

    // ---- S4 --------------------------------------------------------------------------------

    [Fact]
    public void S4_area_by_list_name_or_nlu_resolves_to_a_district()
    {
        var a = At(ConversationState.ElicitLocation);
        Step(a, "8");                         // list row: Tema
        a.LocationDistrict.Should().Be("Tema");

        var b = At(ConversationState.ElicitLocation);
        Step(b, "i stay near kaneshie");      // no NLU: found inside the sentence
        b.LocationDistrict.Should().Be("Ablekuma Central");

        var c = At(ConversationState.ElicitLocation);
        Step(c, "lapaz side", Nlu("LOCATION_SHARE") with
            { LocationDistrict = "Ablekuma North", LocationRegion = "Greater Accra", LocationText = "lapaz", LocationConfidence = 0.6 });
        c.LocationDistrict.Should().BeNull("low confidence is confirmed first");
        Step(c, "1");
        c.LocationDistrict.Should().Be("Ablekuma North");
        c.LocationText.Should().Be("Lapaz");
    }

    // ---- S5 corrections -------------------------------------------------------------------

    [Fact]
    public void S5_change_something_goes_back_to_that_slot_then_returns_to_the_summary()
    {
        var s = At(ConversationState.ConfirmProfile);
        Step(s, "2").Replies.Single().List.Should().HaveCount(5);
        Step(s, "5");                                  // My area
        s.State.Should().Be(ConversationState.ElicitLocation);
        s.LocationDistrict.Should().BeNull();

        var r = Step(s, "Tema");
        s.State.Should().Be(ConversationState.ConfirmProfile);
        r.Replies.Single().Text.Should().Contain("Tema");
        s.Skills.Should().HaveCount(1, "other slots are untouched");
    }

    [Fact]
    public void S5_a_correction_carrying_the_new_value_is_applied_directly()
    {
        var s = At(ConversationState.ConfirmProfile);
        var r = Step(s, "no my area is tema", Nlu("CORRECTION", 0.94) with
            { LocationDistrict = "Tema", LocationRegion = "Greater Accra", LocationText = "tema", LocationConfidence = 0.95 });

        r.Intent.Should().Be(Intent.ChangeProfile);
        s.LocationDistrict.Should().Be("Tema");
        s.State.Should().Be(ConversationState.ConfirmProfile);
        r.Replies.Single().Text.Should().StartWith("Updated");
    }

    [Fact]
    public void S5_changing_the_trade_re_elicits_its_skills()
    {
        var s = At(ConversationState.ConfirmProfile);
        Step(s, "2");
        Step(s, "1");                                  // My work
        s.State.Should().Be(ConversationState.ElicitTrade);
        s.Skills.Should().BeEmpty();
        Step(s, "2");                                  // Carpentry & Woodwork
        Step(s, "1");
        Step(s, "done");
        s.State.Should().Be(ConversationState.ConfirmProfile, "experience, availability and area were kept");
    }

    // ---- 3-strike clarification ------------------------------------------------------------

    [Fact]
    public void Third_consecutive_failure_in_a_state_offers_human_handoff()
    {
        var s = At(ConversationState.ElicitTrade);
        var nothing = Nlu("SKILL_DECLARE", 0.4);

        var r1 = Step(s, "asdf", nothing);
        var r2 = Step(s, "qwer", nothing);
        var r3 = Step(s, "zxcv", nothing);

        r1.Intent.Should().Be(Intent.Repair);
        r2.Intent.Should().Be(Intent.Repair);
        r3.Intent.Should().Be(Intent.HumanHandoff);
        r3.HandoffRequested.Should().BeTrue();
        r3.IsRepair.Should().BeTrue();
        r3.Replies.Should().HaveCount(2, "the handoff message, then the question again");
        s.Strikes.Should().Be(0, "the count starts again after a handoff");
        s.RepairCount.Should().Be(3);
    }

    [Fact]
    public void Progress_resets_the_strike_count()
    {
        var s = At(ConversationState.ElicitTrade);
        Step(s, "asdf", Nlu("SKILL_DECLARE", 0.4));
        Step(s, "qwer", Nlu("SKILL_DECLARE", 0.4));
        Step(s, "1");                                  // a valid answer: on to S2
        s.Strikes.Should().Be(0);
        Step(s, "nonsense", Nlu("SKILL_DECLARE", 0.3)).Intent.Should().Be(Intent.Repair, "a fresh count in S2");
    }

    // ---- Global commands and NLU-recognised requests ---------------------------------------

    [Fact]
    public void Help_keeps_the_state_and_repeats_the_question()
    {
        var s = At(ConversationState.ElicitLocation);
        var r = Step(s, "help");
        r.Intent.Should().Be(Intent.Help);
        s.State.Should().Be(ConversationState.ElicitLocation);
        r.Replies.Should().HaveCount(2);
        r.Replies[1].List.Should().NotBeNull();
    }

    [Fact]
    public void Free_text_help_request_is_recognised_by_the_NLU()
    {
        var s = At(ConversationState.ElicitTrade);
        var r = Step(s, "pls i no understand", Nlu("HELP_REQUEST", 0.97));
        r.Intent.Should().Be(Intent.Help);
        r.IsRepair.Should().BeFalse();
    }

    [Fact]
    public void An_NLU_exit_intent_is_confirmed_before_opting_out()
    {
        var s = At(ConversationState.ElicitSkills);
        Step(s, "later", Nlu("EXIT", 0.9)).Intent.Should().Be(Intent.Clarify);
        s.State.Should().Be(ConversationState.ElicitSkills);

        var stay = Step(s, "2");
        stay.OptedOut.Should().BeFalse();
        s.State.Should().Be(ConversationState.ElicitSkills);

        Step(s, "leave me alone", Nlu("EXIT", 0.9));
        Step(s, "1").OptedOut.Should().BeTrue();
    }

    [Fact]
    public void Stop_opts_out_from_any_state_and_then_stays_silent_until_start()
    {
        var s = At(ConversationState.ElicitExperience);
        Step(s, "stop").OptedOut.Should().BeTrue();
        Step(s, "hello").Replies.Should().BeEmpty();
        Step(s, "help").Replies.Should().BeEmpty();

        var r = Step(s, "start");
        r.Session.State.Should().Be(ConversationState.Welcome);
        r.Session.ConsentGiven.Should().BeFalse("consent is asked again");
    }

    [Fact]
    public void Delete_my_data_is_confirmed_then_requested_FR16()
    {
        var s = At(ConversationState.ConfirmProfile);
        Step(s, "delete my data").Replies.Single().Buttons.Should().HaveCount(2);
        var r = Step(s, "1");
        r.DeleteRequested.Should().BeTrue();
        r.Intent.Should().Be(Intent.DeleteData);
    }

    [Fact]
    public void Delete_can_be_cancelled_and_works_even_when_opted_out()
    {
        var s = At(ConversationState.ElicitLocation);
        Step(s, "delete");
        var keep = Step(s, "2");
        keep.DeleteRequested.Should().BeFalse();
        s.State.Should().Be(ConversationState.ElicitLocation);

        Step(s, "stop");
        Step(s, "delete").Replies.Should().NotBeEmpty();
        Step(s, "1").DeleteRequested.Should().BeTrue();
    }

    [Fact]
    public void Update_profile_after_confirmation_offers_the_change_menu_FR15()
    {
        var s = At(ConversationState.ConfirmProfile);
        Step(s, "1");
        s.State.Should().Be(ConversationState.Active);

        var r = Step(s, "update my profile");
        r.Intent.Should().Be(Intent.UpdateProfile);
        r.Replies.Single().List.Should().HaveCount(5);

        Step(s, "skills");
        s.State.Should().Be(ConversationState.ElicitSkills);
        s.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Update_before_the_profile_exists_just_continues()
    {
        var s = At(ConversationState.ElicitLocation);
        Step(s, "update").Replies.Should().HaveCount(2);
        s.State.Should().Be(ConversationState.ElicitLocation);
    }

    [Fact]
    public void Whole_message_match_avoids_false_stop_command()
    {
        var s = At(ConversationState.ElicitSkills);
        var r = Step(s, "I can stop leaks", Nlu("SKILL_DECLARE", 0.9, new SkillCandidate("Pipe fitting", 0.9)));
        r.OptedOut.Should().BeFalse();
        s.Skills.Select(k => k.CanonicalName).Should().Contain("Pipe fitting");
    }

    [Fact]
    public void Feedback_while_active_is_acknowledged()
    {
        var s = At(ConversationState.ConfirmProfile);
        Step(s, "1");
        Step(s, "this is helpful thanks", Nlu("FEEDBACK_PROVIDE", 0.92)).Intent.Should().Be(Intent.ProvideFeedback);
    }

    // ---- NLU gating ------------------------------------------------------------------------

    [Fact]
    public void ShouldAnalyze_post_consent_free_text_only()
    {
        var s = NewSession();
        s.State = ConversationState.Welcome;
        ConversationFsm.ShouldAnalyze(s, "yes i agree").Should().BeFalse("no NLU before consent (DL-010)");

        s.ConsentGiven = true;
        s.State = ConversationState.ElicitLocation;
        ConversationFsm.ShouldAnalyze(s, "i stay at madina").Should().BeTrue();
        ConversationFsm.ShouldAnalyze(s, " 2 ").Should().BeFalse("menu number");
        ConversationFsm.ShouldAnalyze(s, "1, 3 and 4").Should().BeFalse("menu numbers");
        ConversationFsm.ShouldAnalyze(s, "1", isInteractiveReply: true).Should().BeFalse("a tap");
        ConversationFsm.ShouldAnalyze(s, "  ").Should().BeFalse();

        s.State = ConversationState.OptedOut;
        ConversationFsm.ShouldAnalyze(s, "hello").Should().BeFalse();
    }

    // ---- Message constraints (NFR-05, NFR-07) ----------------------------------------------

    [Fact]
    public void Every_menu_fits_WhatsApp_list_and_button_limits()
    {
        DialoguePrompts.AskTrade(Catalog).List.Should().HaveCountLessOrEqualTo(10);
        DialoguePrompts.AskLocation(Catalog).List.Should().HaveCountLessOrEqualTo(10);
        foreach (var trade in Catalog.Trades)
        {
            var rows = DialoguePrompts.AskSkills(trade, Array.Empty<string>()).List!;
            rows.Should().HaveCountLessOrEqualTo(10);
            rows.Should().OnlyContain(r => r.Title.Length <= 24, $"row titles in {trade.Id}");
        }
        DialoguePrompts.AskTrade(Catalog).List!.Should().OnlyContain(r => r.Title.Length <= 24);
        foreach (var m in new[] { DialoguePrompts.Welcome(), DialoguePrompts.AskExperience(), DialoguePrompts.AskAvailability(),
                     DialoguePrompts.AskDelete(), DialoguePrompts.AskExit() })
            m.Buttons!.Should().OnlyContain(b => b.Title.Length <= 20).And.HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void Prompts_stay_within_500_characters()
    {
        var s = At(ConversationState.ConfirmProfile);
        foreach (var m in new[] { DialoguePrompts.Welcome(), DialoguePrompts.Help(), DialoguePrompts.Summary(s),
                     DialoguePrompts.AskTrade(Catalog), DialoguePrompts.AskLocation(Catalog), DialoguePrompts.ProfileSaved() })
            m.Text.Length.Should().BeLessOrEqualTo(OutboundMessage.MaxLength);
    }
}
