using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CompanyCode).HasMaxLength(30);
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.Password).HasMaxLength(512).IsRequired();
            e.Property(x => x.AccessToken).HasMaxLength(2048);
            e.Property(x => x.RefreshToken).HasMaxLength(2048);
            e.Property(x => x.CompanyName).HasMaxLength(512);
            e.Property(x => x.TaxCode).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.GroupId).HasMaxLength(64);
            e.Property(x => x.PhoneNumber).HasMaxLength(32);
            e.Property(x => x.UserDataJson);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.CompanyCode).IsUnique();
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(64).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.IsSold).HasDefaultValue(true);
            e.Property(x => x.KyHieu).HasMaxLength(32);
            e.Property(x => x.NbMst).HasMaxLength(32);
            e.Property(x => x.NguoiBan).HasMaxLength(512);
            e.Property(x => x.NguoiMua).HasMaxLength(512);
            e.Property(x => x.MstMua).HasMaxLength(32);
            e.Property(x => x.Dvtte).HasMaxLength(16);
            e.Property(x => x.XmlBaseName).HasMaxLength(128);
            e.HasIndex(x => new { x.CompanyId, x.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<BackgroundJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.LastError).HasMaxLength(1024);
            e.Property(x => x.ResultPath).HasMaxLength(2048);
            e.Property(x => x.ExportKey).HasMaxLength(64);
            e.Property(x => x.PayloadJson).HasMaxLength(8192);
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.CompanyId);
        });

        modelBuilder.HasDbFunction(
            typeof(DiacriticsHelper).GetMethod(nameof(DiacriticsHelper.RemoveDiacriticsSql), new[] { typeof(string) })!);
    }
}
