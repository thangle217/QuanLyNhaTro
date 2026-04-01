using Microsoft.EntityFrameworkCore;
using DoAnSE104.Data;
using DoAnSE104.Configurations;
using CloudinaryDotNet;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text;
using DoAnSE104.Services;
using DoAnSE104.Services.Interfaces;
using DoAnSE104.Models;
using DoAnSE104.Helpers;
using Npgsql;

// Neon/PostgreSQL with Npgsql is strict about DateTime.Kind for timestamptz.
// The current project uses DateTime.Now/Today widely, so keep legacy timestamp
// behavior for demo deployment without rewriting every model/controller.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: true);

// ─── DB ───────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    var provider = builder.Configuration["Database:Provider"] ?? "SqlServer";
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

    if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase))
    {
        connectionString = NormalizePostgresConnectionString(connectionString);

        options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null
        ));
    }
    else
    {
        options.UseSqlServer(connectionString, sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null
        ));
    }
});

// ─── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ─── JWT Authentication ───────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)
            ),

            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

// ─── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IRentalPeriodResetService, RentalPeriodResetService>();
builder.Services.AddScoped<IMonthlyInvoiceService, MonthlyInvoiceService>();
builder.Services.AddScoped<IThongBaoService, ThongBaoService>();
builder.Services.AddScoped<INotificationEmailService, NotificationEmailService>();
builder.Services.AddScoped<IDeleteValidationService, DeleteValidationService>();
builder.Services.AddHostedService<EmailReminderBackgroundService>();
builder.Services.AddHostedService<MonthlyInvoiceBackgroundService>();

// ─── Controllers ──────────────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    // Tránh ASP.NET tự bắt buộc navigation property như NhaTro, LoaiPhong, TrangThai...
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
})
.ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(x => x.Value != null && x.Value.Errors.Count > 0)
            .SelectMany(x => x.Value!.Errors.Select(error =>
            {
                var fieldName = x.Key;

                var errorMessage = string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "Dữ liệu không hợp lệ"
                    : error.ErrorMessage;

                return string.IsNullOrWhiteSpace(fieldName)
                    ? errorMessage
                    : $"{fieldName}: {errorMessage}";
            }))
            .ToList();

        var message = errors.Any()
            ? string.Join("; ", errors)
            : "Dữ liệu gửi lên không hợp lệ";

        return new BadRequestObjectResult(ApiResponse<object>.Loi(message));
    };
});

builder.Services.AddEndpointsApiExplorer();

// ─── Swagger với JWT ──────────────────────────────────────────────────────────
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DoAnSE104 API",
        Version = "v1"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization. Nhập: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ─── Cloudinary ───────────────────────────────────────────────────────────────
builder.Services.Configure<CloudinarySettings>(
    builder.Configuration.GetSection("CloudinarySettings")
);

builder.Services.AddSingleton<Cloudinary>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<CloudinarySettings>>().Value;

    var account = new Account(
        settings.CloudName,
        settings.ApiKey,
        settings.ApiSecret
    );

    return new Cloudinary(account);
});

// ─── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// ─── Tự động cập nhật database + Seed Admin mặc định ──────────────────────────
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();

        // Tự động tạo/cập nhật database theo migration
        var recreateDatabase = builder.Configuration.GetValue<bool>("Database:RecreateOnStartup");

        if (recreateDatabase)
        {
            context.Database.EnsureDeleted();
        }

        context.Database.EnsureCreated();

        if (context.Database.IsSqlServer())
        {
            context.EnsureCustomSchema();
        }

        if (!context.TrangThai.Any())
        {
            context.TrangThai.AddRange(
                new TrangThai { TenTrangThai = "Còn trống" },
                new TrangThai { TenTrangThai = "Đã thuê" },
                new TrangThai { TenTrangThai = "Đang sửa chữa" },
                new TrangThai { TenTrangThai = "Ngưng hoạt động" }
            );

            context.SaveChanges();
        }

        // Tạo tài khoản Admin mặc định nếu chưa có
        if (!context.Users.Any(u => u.TenDangNhap == "Admin"))
        {
            context.Users.Add(new User
            {
                TenDangNhap = "Admin",
                HoTen = "Administrator",
                Email = "admin@example.com",
                SoDienThoai = "0123456789",
                VaiTro = "Admin",
                MatKhau = BCrypt.Net.BCrypt.HashPassword("Admin123")
            });

            context.SaveChanges();
        }

        if (builder.Configuration.GetValue<bool>("Database:SeedSampleData"))
        {
            InvokeSampleDataSeederIfAvailable(context);
        }

        EnsureStoredPasswordsAreHashed(context);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();

        logger.LogError(ex, "Lỗi khi tự động cập nhật database hoặc seed dữ liệu mặc định. Inner: {InnerMessage}", ex.InnerException?.Message);
    }
}

// ─── Middleware Pipeline ───────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.MapFallbackToFile("index.html");

app.Run();

static void InvokeSampleDataSeederIfAvailable(ApplicationDbContext context)
{
    var seederType = typeof(ApplicationDbContext).Assembly.GetType("DoAnSE104.Data.SampleDataSeeder");
    var seedMethod = seederType?.GetMethod(
        "Seed",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
        binder: null,
        types: new[] { typeof(ApplicationDbContext) },
        modifiers: null);

    seedMethod?.Invoke(null, new object[] { context });
}

static void EnsureStoredPasswordsAreHashed(ApplicationDbContext context)
{
    var usersWithPlainPassword = context.Users
        .Where(u => !string.IsNullOrWhiteSpace(u.MatKhau))
        .AsEnumerable()
        .Where(u => !IsBcryptHash(u.MatKhau))
        .ToList();

    if (usersWithPlainPassword.Count == 0) return;

    foreach (var user in usersWithPlainPassword)
    {
        user.MatKhau = BCrypt.Net.BCrypt.HashPassword(user.MatKhau);
    }

    context.SaveChanges();
}

static bool IsBcryptHash(string value)
{
    return value.Length == 60
        && (value.StartsWith("$2a$") || value.StartsWith("$2b$") || value.StartsWith("$2y$"));
}

static string NormalizePostgresConnectionString(string connectionString)
{
    if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        && !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return connectionString;
    }

    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':', 2);
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? string.Empty),
        Password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? string.Empty),
        SslMode = SslMode.Require
    };

    if (!uri.IsDefaultPort)
    {
        builder.Port = uri.Port;
    }

    var query = uri.Query.TrimStart('?');
    foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = part.Split('=', 2);
        var key = Uri.UnescapeDataString(pair[0]);
        var value = Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? string.Empty);

        if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
        {
            builder.SslMode = value.Equals("disable", StringComparison.OrdinalIgnoreCase)
                ? SslMode.Disable
                : SslMode.Require;
        }
    }

    return builder.ConnectionString;
}
