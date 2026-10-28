using JobMatch.Domain.Conversation;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace JobMatch.Infrastructure.Cv;

/// <summary>Turns a <see cref="CvContent"/> into a PDF file.</summary>
public interface ICvRenderer
{
    byte[] Render(CvContent cv);
}

/// <summary>
/// One A4 page, plain and printable in black and white (Slice 6, DL-019). Generated on demand and
/// never stored: only the CV answers are kept, so edits simply produce a new file. No photo, date
/// of birth, ID or home address. QuestPDF Community licence (free for this use).
/// </summary>
public class CvPdfRenderer : ICvRenderer
{
    private const string Accent = "#0F6B4F";
    private const string Muted = "#555555";

    static CvPdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // The bundled Lato font covers the Latin text we print; never fail a CV over one glyph.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    public byte[] Render(CvContent cv) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(18, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontSize(11).FontColor("#1A1A1A"));

        page.Content().Column(col =>
        {
            col.Spacing(3);
            col.Item().Text(cv.FullName).FontSize(24).Bold();
            if (cv.Trade is not null)
                col.Item().Text(cv.Trade).FontSize(13).SemiBold().FontColor(Accent);
            var contact = new List<string>();
            if (cv.Phone is not null) contact.Add($"Phone: {cv.Phone} (WhatsApp)");
            if (cv.Areas.Count > 0) contact.Add(string.Join(", ", cv.Areas));
            if (contact.Count > 0)
                col.Item().Text(string.Join("  ·  ", contact)).FontSize(10.5f).FontColor(Muted);

            if (cv.About is not null)
                col.Item().PaddingTop(8).BorderLeft(3).BorderColor(Accent).Background("#F3F7F5")
                    .PaddingVertical(6).PaddingHorizontal(10).Text(cv.About);

            Section(col, "Skills", c =>
            {
                Bullets(c, cv.Skills);
                if (cv.Experience is not null)
                    c.Item().PaddingTop(2).Text($"Experience: {cv.Experience}").FontColor(Muted);
            });

            Section(col, "Work experience", c =>
            {
                if (cv.Jobs.Count == 0)
                    c.Item().Text($"{cv.Experience ?? "Several years"} of experience in {cv.Trade ?? "this work"}.");
                foreach (var job in cv.Jobs)
                {
                    c.Item().Text(job.Description).Bold();
                    if (job.Period is not null)
                        c.Item().PaddingBottom(4).Text(job.Period).FontColor(Muted);
                }
            });

            if (cv.Training.Count > 0)
                Section(col, "Training", c => Bullets(c, cv.Training));
            if (cv.Education is not null)
                Section(col, "Education", c => Bullets(c, new[] { cv.Education }));
            if (cv.Languages.Count > 0)
                Section(col, "Languages", c => Bullets(c, cv.Languages));

            var availability = new List<string>();
            if (cv.Availability is not null) availability.Add(cv.Availability);
            if (cv.Areas.Count > 0) availability.Add("Areas: " + string.Join(", ", cv.Areas));
            if (availability.Count > 0)
                Section(col, "Availability", c => Bullets(c, availability));
        });

        page.Footer().AlignCenter().Text("Made with JobMatch Ghana · details provided by the worker")
            .FontSize(8).FontColor("#888888");
    })).GeneratePdf();

    private static void Section(ColumnDescriptor col, string title, Action<ColumnDescriptor> body)
    {
        col.Item().PaddingTop(12).BorderBottom(1).BorderColor("#C9D6D0").PaddingBottom(2)
            .Text(title.ToUpperInvariant()).FontSize(11.5f).Bold().FontColor(Accent).LetterSpacing(0.05f);
        col.Item().PaddingTop(4).Column(c =>
        {
            c.Spacing(2);
            body(c);
        });
    }

    private static void Bullets(ColumnDescriptor col, IEnumerable<string> items)
    {
        foreach (var item in items)
            col.Item().Row(row =>
            {
                row.ConstantItem(12).Text("•");
                row.RelativeItem().Text(item);
            });
    }
}
