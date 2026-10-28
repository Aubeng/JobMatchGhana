using FluentAssertions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Cv;
using Xunit;

namespace JobMatch.Tests;

/// <summary>The optional CV builder (Slice 6, DL-019): C0-C8, its entry points and the PDF.</summary>
public class CvBuilderTests
{
    private static readonly DialogueCatalog Catalog = TestCatalog.Instance;

    private static FsmStepResult Step(ConversationSession s, string text) => ConversationFsm.Step(s, text, null, Catalog);

    /// <summary>A worker whose profile is confirmed and who is resting in Active.</summary>
    private static ConversationSession Confirmed()
    {
        var s = new ConversationSession
        {
            WaId = "233240000000", ConsentGiven = true, ProfileConfirmed = true, State = ConversationState.Active,
            TradeId = "construction", TradeName = "Construction & Building",
            ExperienceYears = 3, ExperienceText = "1 to 5 years", Availability = "Full-time", AreasDone = true
        };
        s.Skills.Add(new CapturedSkill { CanonicalName = "Block laying", SourceUtterance = "list:1", Confidence = 1 });
        s.Skills.Add(new CapturedSkill { CanonicalName = "Plastering", SourceUtterance = "list:2", Confidence = 1 });
        s.Areas.Add(new AreaChoice { District = "La Nkwantanang Madina", Label = "Madina" });
        s.Areas.Add(new AreaChoice { District = "Adentan", Label = "Adenta" });
        s.LocationDistrict = "La Nkwantanang Madina"; s.LocationText = "Madina";
        return s;
    }

    /// <summary>Answers every CV question; returns the C8 confirmation result.</summary>
    private static FsmStepResult BuildCv(ConversationSession s)
    {
        Step(s, "cv");
        Step(s, "1");                                                    // consent
        Step(s, "kwame asante");
        Step(s, "1");                                                    // WhatsApp number on the CV
        Step(s, "mason for asante builders at madina 2019 to 2023");
        Step(s, "done");
        Step(s, "nvti grade 1 masonry");
        Step(s, "done");
        Step(s, "edu_jhs");
        Step(s, "twi, english");
        Step(s, "done");
        Step(s, "1");                                                    // keep the suggested line
        s.State.Should().Be(ConversationState.CvReview);
        return Step(s, "1");
    }

    [Fact]
    public void The_whole_cv_flow_collects_each_part_then_sends_the_cv()
    {
        var s = Confirmed();
        var states = new List<ConversationState>();
        Step(s, "cv"); states.Add(s.State);
        Step(s, "1"); states.Add(s.State);
        Step(s, "kwame asante"); states.Add(s.State);
        Step(s, "1"); states.Add(s.State);
        Step(s, "mason for asante builders at madina 2019 to 2023"); states.Add(s.State);
        Step(s, "done"); states.Add(s.State);
        Step(s, "skip"); states.Add(s.State);
        Step(s, "JHS"); states.Add(s.State);
        Step(s, "twi and english"); states.Add(s.State);
        Step(s, "done"); states.Add(s.State);
        Step(s, "1"); states.Add(s.State);
        var done = Step(s, "1");

        states.Should().Equal(
            ConversationState.CvConsent, ConversationState.CvName, ConversationState.CvPhone, ConversationState.CvWork,
            ConversationState.CvWork, ConversationState.CvTraining, ConversationState.CvEducation, ConversationState.CvLanguages,
            ConversationState.CvLanguages, ConversationState.CvAbout, ConversationState.CvReview);
        done.SendCv.Should().BeTrue();
        done.Intent.Should().Be(Intent.CvCompleted);
        s.State.Should().Be(ConversationState.Active);

        var cv = s.Cv!;
        cv.Completed.Should().BeTrue();
        cv.ConsentUtc.Should().NotBeNull();
        cv.FullName.Should().Be("Kwame Asante");
        cv.Phone.Should().Be("024 000 0000");
        cv.Jobs.Single().Description.Should().Be("Mason for asante builders at Madina", "known places get their proper name");
        cv.Jobs.Single().Period.Should().Be("2019 – 2023");
        cv.Jobs.Single().SourceUtterance.Should().Be("mason for asante builders at madina 2019 to 2023");
        cv.Training.Should().BeEmpty();
        cv.Education.Should().Be("Junior High School (JHS)");
        cv.Languages.Should().Equal("Twi", "English");
        cv.About.Should().Be("Skilled in block laying and plastering, with 1 to 5 years of experience. " +
                             "Available full-time in Madina and Adenta.");
    }

    [Fact]
    public void Cv_is_offered_once_after_feedback_and_no_means_no()
    {
        var s = Confirmed();
        s.State = ConversationState.Feedback;
        s.AwaitingComment = true;
        var r = Step(s, "skip");
        r.Replies.Last().Text.Should().Contain("free CV");

        var no = Step(s, "2");
        no.Intent.Should().Be(Intent.CvDeclined);
        s.State.Should().Be(ConversationState.Active);

        s.State = ConversationState.Feedback;
        s.AwaitingComment = true;
        Step(s, "skip").Replies.Should().NotContain(m => m.Text.Contains("free CV"), "the offer is made only once");
        Step(s, "cv");
        s.State.Should().Be(ConversationState.CvConsent, "the cv command still works");
    }

    [Fact]
    public void Refusing_cv_consent_keeps_nothing()
    {
        var s = Confirmed();
        Step(s, "cv");
        var r = Step(s, "2");
        r.Intent.Should().Be(Intent.CvDeclined);
        s.Cv.Should().BeNull();
        s.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public void Cv_needs_a_confirmed_profile_first()
    {
        var s = Confirmed();
        s.ProfileConfirmed = false;
        s.State = ConversationState.ElicitLocation;
        var r = Step(s, "cv");
        r.Replies[0].Text.Should().Contain("finish your profile");
        s.State.Should().Be(ConversationState.ElicitLocation);
    }

    [Fact]
    public void A_part_can_be_changed_from_the_summary_then_it_returns_there()
    {
        var s = Confirmed();
        BuildCv(s);
        Step(s, "cv").Replies.Single().Text.Should().Contain("Kwame Asante", "an existing CV goes straight to its summary");
        s.State.Should().Be(ConversationState.CvReview);

        Step(s, "2").Replies.Single().List.Should().HaveCount(7);
        Step(s, "1");                                   // Name
        s.State.Should().Be(ConversationState.CvName);
        var r = Step(s, "Ama Mensah");
        s.State.Should().Be(ConversationState.CvReview, "every other part is still filled");
        r.Replies.Single().Text.Should().Contain("Ama Mensah");
        s.Cv!.Languages.Should().Equal("Twi", "English");
    }

    [Fact]
    public void Phone_can_be_another_number_or_none()
    {
        var s = Confirmed();
        Step(s, "cv"); Step(s, "1"); Step(s, "Kwame Asante");
        Step(s, "2");
        Step(s, "not a number").IsRepair.Should().BeTrue();
        Step(s, "+233 24 123 4567");
        s.Cv!.Phone.Should().Be("024 123 4567");
        s.State.Should().Be(ConversationState.CvWork);

        var t = Confirmed();
        Step(t, "cv"); Step(t, "1"); Step(t, "Kwame Asante");
        Step(t, "3");
        t.Cv!.Phone.Should().BeNull();
        t.Cv.PhoneAnswered.Should().BeTrue();
    }

    [Fact]
    public void Lists_stop_at_their_maximum_and_names_are_checked()
    {
        var s = Confirmed();
        Step(s, "cv"); Step(s, "1");
        Step(s, "2").IsRepair.Should().BeTrue("a menu number isn't a name");
        Step(s, "Kwame Asante"); Step(s, "3");
        Step(s, "driver 2015 to 2017"); Step(s, "mate 2017 to 2018");
        Step(s, "mason since 2019");
        s.Cv!.Jobs.Should().HaveCount(ConversationFsm.MaxCvJobs);
        s.State.Should().Be(ConversationState.CvTraining, "the third job ends C3");
    }

    [Fact]
    public void Global_commands_still_work_inside_the_cv_and_no_nlu_runs_there()
    {
        var s = Confirmed();
        Step(s, "cv"); Step(s, "1");
        ConversationFsm.ShouldAnalyze(s, "kwame asante").Should().BeFalse("CV answers are not intents");
        var help = Step(s, "help");
        help.Replies.Last().Text.Should().Contain("What name should be on your CV");
        Step(s, "stop").OptedOut.Should().BeTrue();
    }

    [Theory]
    [InlineData("mason for asante builders at madina 2019 to 2023", "Mason for asante builders at madina", "2019 – 2023")]
    [InlineData("Driver at Kaneshie from 2018 - present", "Driver at Kaneshie", "2018 – now")]
    [InlineData("tiler since 2020", "Tiler", "since 2020")]
    [InlineData("I worked as a cleaner for 3 years", "I worked as a cleaner", "3 years")]
    [InlineData("apprentice carpenter", "Apprentice carpenter", null)]
    public void Jobs_keep_the_workers_words_and_pull_out_the_period(string text, string description, string? period)
    {
        var job = ConversationFsm.ParseJob(text);
        job.Description.Should().Be(description);
        job.Period.Should().Be(period);
        job.SourceUtterance.Should().Be(text);
    }

    [Theory]
    [InlineData("0241234567", "024 123 4567")]
    [InlineData("+233 24 123 4567", "024 123 4567")]
    [InlineData("233241234567", "024 123 4567")]
    [InlineData("12345", null)]
    [InlineData("call me", null)]
    public void Ghana_phone_numbers_are_recognised_and_formatted(string text, string? expected)
        => ConversationFsm.ParsePhone(text).Should().Be(expected);

    [Fact]
    public void The_renderer_makes_a_one_file_pdf_named_after_the_worker()
    {
        var s = Confirmed();
        BuildCv(s);
        var content = CvContent.From(s);
        content.FileName.Should().Be("CV - Kwame Asante.pdf");
        content.Skills.Should().Equal("Block laying", "Plastering");
        content.Areas.Should().Equal("Madina", "Adenta");
        content.Training.Should().Equal("NVTI grade 1 masonry");

        var pdf = new CvPdfRenderer().Render(content);
        pdf.Length.Should().BeGreaterThan(1000);
        // Set JOBMATCH_CV_SAMPLE to a file path to look at the result (docs/cv-sample.pdf comes from here).
        if (Environment.GetEnvironmentVariable("JOBMATCH_CV_SAMPLE") is { Length: > 0 } path)
            File.WriteAllBytes(path, pdf);
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void Cv_messages_respect_whatsapp_limits()
    {
        var s = Confirmed();
        BuildCv(s);
        foreach (var m in new[]
                 {
                     DialoguePrompts.OfferCv(), DialoguePrompts.AskCvConsent(), DialoguePrompts.AskCvPhone("024 000 0000"),
                     DialoguePrompts.AskCvEducation(), DialoguePrompts.AskCvLanguages(Array.Empty<string>()),
                     DialoguePrompts.AskCvLanguages(new[] { "Twi" }), DialoguePrompts.ChooseCvEdit(),
                     DialoguePrompts.AskCvAbout(ConversationFsm.AboutLine(s)), DialoguePrompts.CvSummary(s)
                 })
        {
            m.Buttons?.Should().OnlyContain(b => b.Title.Length <= 20);
            m.List?.Should().HaveCountLessOrEqualTo(10).And.OnlyContain(r => r.Title.Length <= 24);
            (m.ListButton?.Length ?? 0).Should().BeLessOrEqualTo(20);
        }
    }
}
