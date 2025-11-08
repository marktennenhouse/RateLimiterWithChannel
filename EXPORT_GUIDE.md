# Export and Import Guide

This guide explains how to export the `PaymentRateLimiter.Core` class library and import it into another project on a different machine under a different Git account.

## Overview

The project is now split into two parts:
1. **PaymentRateLimiter.Core** - Reusable class library (THIS is what you'll export)
2. **PaymentChannelDemo** - Example web API implementation (reference implementation)

## Step 1: Export from This Machine

### Option A: Export Just the Class Library (Recommended)

```bash
# Navigate to the PaymentRateLimiter.Core directory
cd PaymentRateLimiter.Core

# Create a zip of the class library
Compress-Archive -Path * -DestinationPath ../PaymentRateLimiter.Core.zip

# Or copy the entire PaymentRateLimiter.Core folder to a USB drive, cloud storage, etc.
```

The `PaymentRateLimiter.Core` folder contains:
- `/Models/` - 4 model files
- `/Services/` - 4 service files
- `PaymentRateLimiter.Core.csproj` - Project file
- `README.md` - Usage instructions

**Total size**: ~50KB (source code only)

### Option B: Export the Entire Solution (For Reference)

```bash
# From the root directory
cd "C:\Users\markt\OneDrive\Documents\Cursor Projects\Rate Limiter 2"

# Create a zip of everything
Compress-Archive -Path * -DestinationPath PaymentRateLimiterComplete.zip
```

This includes both the class library and the example web API.

---

## Step 2: Transfer to New Machine

Copy the `PaymentRateLimiter.Core` folder (or zip file) to your new machine using:
- USB drive
- Cloud storage (OneDrive, Dropbox, Google Drive)
- Network share
- Email attachment
- Git repository (see Step 3)

---

## Step 3: Import on New Machine

### Scenario: Adding to Existing ASP.NET Core Project

1. **Copy the PaymentRateLimiter.Core folder** to your solution directory:

```
YourExistingSolution/
├── YourExistingProject/
│   ├── Controllers/
│   ├── Program.cs
│   └── YourExistingProject.csproj
│
├── PaymentRateLimiter.Core/          ← COPY HERE
│   ├── Models/
│   ├── Services/
│   └── PaymentRateLimiter.Core.csproj
│
└── YourExistingSolution.sln
```

2. **Add the class library to your solution**:

```bash
# Navigate to your solution directory
cd /path/to/YourExistingSolution

# Add the class library project to your solution
dotnet sln add PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

3. **Add a project reference from your web project**:

```bash
# Navigate to your web project
cd YourExistingProject

# Add reference to the class library
dotnet add reference ../PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

4. **Register services in your Program.cs**:

```csharp
using PaymentRateLimiter.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Add your existing services...

// Add PaymentRateLimiter services
builder.Services.AddSingleton<PaymentChannelService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();
builder.Services.AddHostedService<PaymentCleanupService>();

// Configure JSON to serialize enums as strings
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
```

5. **Create a controller** (or use the one from this project):

Copy `Controllers/PaymentController.cs` to your project, or create a new one following the pattern in `PaymentRateLimiter.Core/README.md`.

---

## Step 4: Add to Git (Different Account)

### Initialize Git in Your Solution (if not already)

```bash
cd /path/to/YourExistingSolution

# Check if git is already initialized
git status

# If not, initialize
git init
git branch -M main  # or master
```

### Add and Commit the Class Library

```bash
# Stage the class library
git add PaymentRateLimiter.Core/

# Commit it
git commit -m "Add: PaymentRateLimiter.Core class library

- Payment processing with rate limiting (5 concurrent)
- Server-Sent Events support
- Client disconnection handling
- Multi-level cleanup strategy"
```

### Push to Your Git Repository (Different Account)

```bash
# Add your remote (GitHub, GitLab, Azure DevOps, etc.)
git remote add origin https://github.com/your-account/your-repo.git

# Push to your repository
git push -u origin main
```

---

## Step 5: Verify Integration

1. **Build your solution**:

```bash
dotnet build
```

2. **Run your application**:

```bash
dotnet run
```

3. **Test the payment endpoint**:

```bash
curl -X POST https://localhost:7000/api/payment/process \
  -H "Content-Type: application/json" \
  -d '{"amount":99.99,"cardToken":"tok_demo"}' \
  --no-buffer
```

You should see Server-Sent Event messages streaming back.

---

## Alternative: Git Submodule Approach

If you want to keep the class library separate and pull updates:

### On Original Machine:

```bash
# Create a separate Git repository for the class library
cd PaymentRateLimiter.Core
git init
git add .
git commit -m "Initial commit: PaymentRateLimiter.Core library"
git remote add origin https://github.com/your-account/PaymentRateLimiter.Core.git
git push -u origin master
```

### On New Machine:

```bash
# In your existing project
cd YourExistingSolution

# Add as submodule
git submodule add https://github.com/your-account/PaymentRateLimiter.Core.git

# Add project reference
cd YourExistingProject
dotnet add reference ../PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

### Benefits of Submodule:
- ✅ Separate version history
- ✅ Can update independently
- ✅ Reusable across multiple projects
- ✅ Can track updates from original source

---

## Folder Structure After Import

```
YourExistingSolution/
├── .git/                              (Your git repo)
│
├── YourExistingProject/
│   ├── Controllers/
│   │   ├── YourExistingController.cs
│   │   └── PaymentController.cs       (NEW - copied from example)
│   ├── Program.cs                     (MODIFIED - added services)
│   └── YourExistingProject.csproj     (MODIFIED - added reference)
│
├── PaymentRateLimiter.Core/           (IMPORTED)
│   ├── Models/
│   │   ├── PaymentRequest.cs
│   │   ├── PaymentRequestDto.cs
│   │   ├── PaymentStatus.cs
│   │   └── PaymentStatusEnum.cs
│   ├── Services/
│   │   ├── PaymentChannelService.cs
│   │   ├── PaymentStatusService.cs
│   │   ├── PaymentProcessorWorker.cs
│   │   └── PaymentCleanupService.cs
│   ├── PaymentRateLimiter.Core.csproj
│   └── README.md
│
└── YourExistingSolution.sln           (MODIFIED - added Core project)
```

---

## Quick Copy Commands

### From This Machine:

**PowerShell**:
```powershell
# Create export package
$destination = "C:\Temp\PaymentRateLimiterExport"
New-Item -ItemType Directory -Path $destination -Force
Copy-Item -Path PaymentRateLimiter.Core -Destination $destination -Recurse

# Zip it
Compress-Archive -Path $destination\PaymentRateLimiter.Core -DestinationPath "C:\Temp\PaymentRateLimiter.Core.zip"
```

**Bash/WSL**:
```bash
# Create export package
cp -r PaymentRateLimiter.Core /tmp/PaymentRateLimiter.Core
cd /tmp
tar -czf PaymentRateLimiter.Core.tar.gz PaymentRateLimiter.Core
```

### To New Machine:

1. Copy the `PaymentRateLimiter.Core` folder to your solution directory
2. Run: `dotnet sln add PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj`
3. Run: `dotnet add YourProject reference ../PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj`
4. Update Program.cs to register services
5. Test: `dotnet build`

---

## What Gets Committed to Your Git

When you commit the class library to your own Git repository:

```bash
git add PaymentRateLimiter.Core/
git status
```

You'll see:
- `PaymentRateLimiter.Core/Models/` (4 files)
- `PaymentRateLimiter.Core/Services/` (4 files)
- `PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj`
- `PaymentRateLimiter.Core/README.md`

**NOT committed** (automatically ignored by .gitignore):
- `bin/` directory
- `obj/` directory
- Build artifacts

---

## Troubleshooting

### "Project not found" when building

Make sure you've added the project reference:
```bash
dotnet add reference ../PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

### "Type or namespace not found"

Add using statements:
```csharp
using PaymentRateLimiter.Core.Models;
using PaymentRateLimiter.Core.Services;
```

### Services not registered

Verify in Program.cs:
```csharp
builder.Services.AddSingleton<PaymentChannelService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();
builder.Services.AddHostedService<PaymentCleanupService>();
```

### Enum serialized as number instead of string

Add JSON converter in Program.cs:
```csharp
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
```

---

## Version Control Strategy

### Recommended: Separate Repository for Class Library

1. Create a new repository for the class library alone
2. Push PaymentRateLimiter.Core to that repository
3. Reference it in your main project as a Git submodule or project reference
4. Update the library independently

### Alternative: Include in Your Main Repository

1. Copy `PaymentRateLimiter.Core/` to your solution
2. Commit it along with your other projects
3. Treat it as part of your solution

Both approaches work - choose based on whether you plan to reuse the library across multiple solutions.

---

## Contact / Support

For questions about the library, refer to the documentation in the original project:
- `ARCHITECTURE.md` - System architecture
- `FLOW_AND_TIMING.md` - Event flow and timing
- `TESTING.md` - Test scenarios
- `README.md` - General usage

## License

MIT License - free to use in your projects.

