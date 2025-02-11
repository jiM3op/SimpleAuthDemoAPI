using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Negotiate;
using System.Security.Principal;
using System.DirectoryServices.AccountManagement;

var builder = WebApplication.CreateBuilder(args);

// Enable Windows Authentication
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});

// Add CORS services before building the app
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowNextJs",
        policy =>
        {
            policy.WithOrigins("http://localhost:3000") // Allow requests from Next.js
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
});

// Add services
builder.Services.AddDbContext<QuestionDbContext>(options =>
    options.UseInMemoryDatabase("QuestionDb")); // Change to UseSqlServer if using SQL Server

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve;
});

// Add Swagger services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Question API", Version = "v1" });
});

var app = builder.Build();

// Enable CORS before authentication & authorization
app.UseCors("AllowNextJs");

// Enable Swagger UI

    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Question API v1");
        c.RoutePrefix = "swagger"; // Ensures Swagger is accessible at /swagger
    });


// Enable authentication & authorization
app.UseStaticFiles();  // ✅ Ensures Swagger static files load
app.UseAuthentication();
app.UseAuthorization();

// Prepopulate in-memory database with sample questions
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<QuestionDbContext>();
    dbContext.Questions.AddRange(new List<QuestionSimple>
    {
        new QuestionSimple
        {
            QuestionBody = "What is the capital of France?",
            Category = "Geography",
            DifficultyLevel = 20,
            QsChecked = true,
            Answers = new List<AnswerSimple>
            {
                new AnswerSimple { AnswerBody = "Paris", AnswerCorrect = true, AnswerPosition = "a" },
                new AnswerSimple { AnswerBody = "London", AnswerCorrect = false, AnswerPosition = "b" },
                new AnswerSimple { AnswerBody = "Rome", AnswerCorrect = false, AnswerPosition = "c" },
                new AnswerSimple { AnswerBody = "Berlin", AnswerCorrect = false, AnswerPosition = "d" }
            },
            CreatedBy = "System",
            Created = DateTime.UtcNow
        }
    });
    dbContext.SaveChanges();
}

// User Endpoint to Retrieve Current Authenticated User with Group Check
app.MapGet("/api/user", (HttpContext httpContext) =>
{
    var user = httpContext.User.Identity as WindowsIdentity;
    if (user == null) return Results.Unauthorized();

    bool isInGroup = IsUserInGroup(user, "QuizContributers");
    return Results.Ok(new { User = user.Name, IsInQuizContributers = isInGroup });
})
.WithName("GetCurrentUser")
.WithOpenApi()
.RequireAuthorization();

// Question Endpoints
app.MapPost("/api/questions", async ([FromBody] QuestionSimple question, QuestionDbContext db, HttpContext httpContext) =>
{
    var user = httpContext.User.Identity as WindowsIdentity;
    if (user == null || !IsUserInGroup(user, "QuizContributers"))
    {
        return Results.Forbid();
    }

    question.CreatedBy = user.Name;
    question.Created = DateTime.UtcNow;
    db.Questions.Add(question);
    await db.SaveChangesAsync();
    return Results.Created($"/api/questions/{question.Id}", question);
})
.WithName("CreateQuestion")
.WithOpenApi()
.RequireAuthorization();

app.MapGet("/api/questions", async (QuestionDbContext db) =>
{
    var questions = await db.Questions.Include(q => q.Answers).ToListAsync();
    return Results.Ok(questions);
})
.WithName("GetQuestions")
.WithOpenApi()
.RequireAuthorization();

app.MapGet("/api/questions/{id}", async (int id, QuestionDbContext db) =>
{
    var question = await db.Questions.Include(q => q.Answers).FirstOrDefaultAsync(q => q.Id == id);
    return question is not null ? Results.Ok(question) : Results.NotFound();
})
.WithName("GetQuestionById")
.WithOpenApi()
.RequireAuthorization();

app.Run();

// Helper function to check group membership
bool IsUserInGroup(WindowsIdentity? identity, string groupName)
{
    if (identity == null) return false;

    try
    {
        using (var context = new PrincipalContext(ContextType.Machine))
        using (var user = UserPrincipal.FindByIdentity(context, identity.Name))
        {
            if (user != null)
            {
                foreach (var group in user.GetGroups())
                {
                    if (group.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error checking user groups: {ex.Message}");
    }

    return false;
}

// Database Context and Models
public class QuestionDbContext : DbContext
{
    public QuestionDbContext(DbContextOptions<QuestionDbContext> options) : base(options) { }

    public DbSet<QuestionSimple> Questions { get; set; }
    public DbSet<AnswerSimple> Answers { get; set; }
}

public class QuestionSimple
{
    public int Id { get; set; }
    public required string QuestionBody { get; set; }
    public required string Category { get; set; }
    public int DifficultyLevel { get; set; }
    public bool QsChecked { get; set; }
    public List<AnswerSimple> Answers { get; set; } = new();
    public string? CreatedBy { get; set; }
    public DateTime Created { get; set; }
}

public class AnswerSimple
{
    public int Id { get; set; }
    public required string AnswerBody { get; set; }
    public bool AnswerCorrect { get; set; }
    public required string AnswerPosition { get; set; }

    public int QuestionSimpleId { get; set; }

    [JsonIgnore] // Prevents circular reference in JSON serialization
    public QuestionSimple? Question { get; set; }
}
