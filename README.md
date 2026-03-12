# EduAutomation - Cross-Platform Educational Automation App

A production-ready .NET MAUI application for 9th-grade students that integrates
Gmail, Canvas LMS, Infinite Campus, and OpenAI GPT-4 to automate and manage
school assignments with a mandatory human-approval workflow.

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Project Setup in Visual Studio](#project-setup-in-visual-studio)
- [NuGet Package Installation](#nuget-package-installation)
- [API Credentials Setup](#api-credentials-setup)
- [Secure Storage Configuration](#secure-storage-configuration)
- [Project Structure Overview](#project-structure-overview)
- [Platform-Specific Build Instructions](#platform-specific-build-instructions)
- [Running Unit Tests](#running-unit-tests)
- [Debugging Guide](#debugging-guide)
- [API Rate Limit and Token Expiration Handling](#api-rate-limit-and-token-expiration-handling)
- [Troubleshooting Common Issues](#troubleshooting-common-issues)

---

## Prerequisites

- Visual Studio 2022 (version 17.8 or later) with the .NET Multi-platform App UI workload installed
- .NET 8 SDK (download from https://dotnet.microsoft.com/download/dotnet/8.0)
- A Google Cloud Platform account with the Gmail API enabled
- A Canvas LMS account with API token access
- An Infinite Campus student account
- An OpenAI API account with GPT-4 access
- Windows 10/11 (for Windows target), macOS 13+ (for macOS/iOS targets), or Android emulator

---

## Project Setup in Visual Studio

### Step 1 - Create the Solution

1. Open Visual Studio 2022.
2. Click "Create a new project."
3. Search for ".NET MAUI App" in the project template search bar.
4. Select ".NET MAUI App" and click Next.
5. Set the Project Name to `EduAutomation`.
6. Set the Solution Name to `EduAutomationSolution`.
7. Choose a directory for your project and click Next.
8. Select ".NET 8.0" as the target framework.
9. Click Create.

### Step 2 - Add the Unit Test Project

1. Right-click the Solution in Solution Explorer.
2. Select Add > New Project.
3. Search for "xUnit Test Project" and select it.
4. Name it `EduAutomation.Tests`.
5. Target ".NET 8.0" and click Create.
6. Right-click the test project > Add > Project Reference > check `EduAutomation`.

### Step 3 - Configure the Android Manifest (Android target only)

1. Open `Platforms/Android/AndroidManifest.xml`.
2. Add the following permissions inside the `<manifest>` tag:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.RECORD_AUDIO" />
```

### Step 4 - Configure Info.plist (iOS/macOS target only)

1. Open `Platforms/iOS/Info.plist`.
2. Add the following key/value pairs for microphone access (voice transcript feature):

```xml
<key>NSMicrophoneUsageDescription</key>
<string>EduAutomation needs microphone access for voice transcript input in the Data Dump feature.</string>
```

---

## NuGet Package Installation

Open the NuGet Package Manager (Tools > NuGet Package Manager > Manage NuGet Packages for Solution)
and install the following packages into the `EduAutomation` project:

### Core Framework Packages

- `CommunityToolkit.Mvvm` version 8.3.2 or later
  - Provides MVVM source generators, ObservableObject, RelayCommand, and dependency injection helpers.

- `Microsoft.Extensions.Http` version 8.0.0 or later
  - Provides IHttpClientFactory for managed HttpClient lifetimes.

- `Microsoft.Extensions.Logging.Abstractions` version 8.0.0 or later
  - Provides the ILogger interface used throughout all service classes.

### Logging Packages

- `Serilog` version 4.0.0 or later
- `Serilog.Sinks.File` version 6.0.0 or later
- `Serilog.Sinks.Debug` version 3.0.0 or later
- `Serilog.Extensions.Logging` version 8.0.0 or later

### Google Gmail API Packages

- `Google.Apis.Gmail.v1` version 1.68.0 or later
- `Google.Apis.Auth` version 1.68.0 or later
- `Google.Apis.Oauth2.v2` version 1.68.0 or later

### HTTP and Serialization Packages

- `Newtonsoft.Json` version 13.0.3 or later
  - Used for all API response deserialization.

- `Polly` version 8.4.0 or later
  - Provides resilience policies: retry, circuit breaker, and rate limit handling.

- `Microsoft.Extensions.Http.Polly` version 8.0.0 or later

### HTML Scraping (Infinite Campus fallback)

- `HtmlAgilityPack` version 1.11.67 or later
  - Used if Infinite Campus does not expose a REST API for the school district.

### Testing Packages (install into `EduAutomation.Tests` project only)

- `xunit` version 2.9.0 or later
- `xunit.runner.visualstudio` version 2.8.2 or later
- `Moq` version 4.20.72 or later
- `FluentAssertions` version 6.12.0 or later
- `Microsoft.Extensions.Logging.Testing` version 8.0.0 or later

Install all packages via the Package Manager Console with:

```
Install-Package CommunityToolkit.Mvvm -ProjectName EduAutomation
Install-Package Serilog -ProjectName EduAutomation
Install-Package Serilog.Sinks.File -ProjectName EduAutomation
Install-Package Serilog.Sinks.Debug -ProjectName EduAutomation
Install-Package Serilog.Extensions.Logging -ProjectName EduAutomation
Install-Package Google.Apis.Gmail.v1 -ProjectName EduAutomation
Install-Package Google.Apis.Auth -ProjectName EduAutomation
Install-Package Newtonsoft.Json -ProjectName EduAutomation
Install-Package Polly -ProjectName EduAutomation
Install-Package Microsoft.Extensions.Http.Polly -ProjectName EduAutomation
Install-Package HtmlAgilityPack -ProjectName EduAutomation
Install-Package xunit -ProjectName EduAutomation.Tests
Install-Package Moq -ProjectName EduAutomation.Tests
Install-Package FluentAssertions -ProjectName EduAutomation.Tests
```

---

## API Credentials Setup

### Google Gmail API Setup

1. Go to https://console.cloud.google.com and create a new project named `EduAutomation`.
2. Navigate to APIs & Services > Library.
3. Search for "Gmail API" and click Enable.
4. Go to APIs & Services > OAuth consent screen.
   - Select "External" user type.
   - Fill in App Name: `EduAutomation`, User Support Email, and Developer Email.
   - Add scope: `https://www.googleapis.com/auth/gmail.readonly`
   - Add your student Gmail address as a test user.
5. Go to APIs & Services > Credentials > Create Credentials > OAuth 2.0 Client IDs.
   - Application type: Desktop App.
   - Name: `EduAutomation Desktop`.
   - Click Create and download the JSON file.
6. Rename the downloaded file to `google_credentials.json`.
7. Place it in the `Resources/Raw/` folder of the EduAutomation project.
8. Set the file's Build Action to `MauiAsset` in the Properties pane.

### Canvas LMS API Token Setup

1. Log in to your school's Canvas LMS portal.
2. Click your profile picture > Account > Settings.
3. Scroll to "Approved Integrations" and click "+ New Access Token."
4. Purpose: `EduAutomation App`. Set an expiration date if desired.
5. Click "Generate Token" and copy the token immediately (it is shown only once).
6. Store this token using the Secure Storage method described in the next section.
7. Note your Canvas base URL (example: `https://yourschool.instructure.com`).
   This is also stored in secure storage.

### Infinite Campus API Setup

1. Infinite Campus access method depends on your school district's configuration.
2. Some districts expose a student REST API. Contact your school's IT department
   and ask if the Infinite Campus Campus API (REST) is enabled for student accounts.
3. If a REST API is available, you will need:
   - District base URL (example: `https://yourschool.infinitecampus.com`)
   - Your student username and password (stored in secure storage)
4. If no REST API is available, the app uses authenticated web scraping via
   HtmlAgilityPack as a fallback. The scraping module targets the standard
   Infinite Campus student portal HTML structure.

### OpenAI API Key Setup

1. Go to https://platform.openai.com/api-keys.
2. Click "Create new secret key." Name it `EduAutomation`.
3. Copy the key immediately (shown only once).
4. Ensure your account has access to the `gpt-4o` model (GPT-4 class).
5. Store the key using the Secure Storage method described below.

---

## Secure Storage Configuration

The app uses .NET MAUI's built-in `SecureStorage` class, which on each platform
uses the native secure enclave (Keychain on iOS/macOS, Android Keystore on Android,
and Windows Data Protection API on Windows). API keys are NEVER hardcoded.

### First-Run Setup Screen

On first launch, the app presents a Settings/Setup screen where the user enters:

- Canvas Base URL
- Canvas API Token
- Infinite Campus District URL
- Infinite Campus Username
- Infinite Campus Password
- OpenAI API Key

These are stored via:

```csharp
await SecureStorage.Default.SetAsync("canvas_api_token", tokenValue);
await SecureStorage.Default.SetAsync("openai_api_key", openAiKeyValue);
```

And retrieved via:

```csharp
string token = await SecureStorage.Default.GetAsync("canvas_api_token");
```

The Google OAuth token is stored in its own credential file managed by the
`Google.Apis.Auth` library, persisted to the app's local data directory.

### Environment Variable Fallback (Development Only)

During development and testing, you can set environment variables so you do not
need to run the app to seed credentials:

```
EDUAUTO_CANVAS_URL=https://yourschool.instructure.com
EDUAUTO_CANVAS_TOKEN=your_canvas_token_here
EDUAUTO_OPENAI_KEY=sk-your-openai-key-here
EDUAUTO_IC_URL=https://yourschool.infinitecampus.com
EDUAUTO_IC_USER=your_username
EDUAUTO_IC_PASS=your_password
```

Set these in Visual Studio via Project Properties > Debug > Environment Variables.

---

## Project Structure Overview

```
EduAutomationSolution/
  EduAutomation/
    MauiProgram.cs                  - DI container and app bootstrap
    App.xaml / App.xaml.cs          - Application entry point
    AppShell.xaml / AppShell.xaml.cs - Tab navigation shell
    Models/
      Assignment.cs                 - Canvas/IC assignment data model
      EmailAlert.cs                 - Gmail alert data model
      ReviewItem.cs                 - AI-generated content pending review
      DataDumpItem.cs               - Raw data input model
      CourseInfo.cs                 - Canvas course metadata
    Services/
      IGmailService.cs              - Gmail service interface
      GmailService.cs               - Gmail API implementation
      ICanvasService.cs             - Canvas service interface
      CanvasService.cs              - Canvas REST API implementation
      IInfiniteCampusService.cs     - IC service interface
      InfiniteCampusService.cs      - IC REST + scraping implementation
      IOpenAIService.cs             - OpenAI service interface
      OpenAIService.cs              - GPT-4 integration with guardrails
      ILoggingService.cs            - Logging service interface
      LoggingService.cs             - Serilog-backed logging implementation
      ISecureConfigService.cs       - Credential service interface
      SecureConfigService.cs        - SecureStorage wrapper
    ViewModels/
      BaseViewModel.cs              - ObservableObject base with logging
      DashboardViewModel.cs         - Dashboard summary data
      GmailViewModel.cs             - Gmail alerts list
      AssignmentsViewModel.cs       - Missing assignments list
      DataDumpViewModel.cs          - Raw data input handler
      ReviewViewModel.cs            - Review, edit, and submit workflow
      SettingsViewModel.cs          - API credential management
    Views/
      DashboardPage.xaml/.cs        - Dashboard tab
      GmailPage.xaml/.cs            - Gmail alerts tab
      AssignmentsPage.xaml/.cs      - Missing assignments tab
      DataDumpPage.xaml/.cs         - Data dump tab
      ReviewPage.xaml/.cs           - Review and submit tab
      SettingsPage.xaml/.cs         - First-run and ongoing settings
    Converters/
      BoolToColorConverter.cs       - XAML value converter
      StatusToIconConverter.cs      - Assignment status icon converter
    Helpers/
      ApiRateLimitHandler.cs        - Polly-based retry and rate-limit handler
      TokenRefreshHandler.cs        - OAuth token refresh delegating handler
      PromptGuardrails.cs           - OpenAI input/output validation
    Resources/
      Raw/
        google_credentials.json     - Google OAuth client secrets (gitignored)
  EduAutomation.Tests/
    Services/
      GmailServiceTests.cs
      CanvasServiceTests.cs
      OpenAIServiceTests.cs
      InfiniteCampusServiceTests.cs
    ViewModels/
      ReviewViewModelTests.cs
      AssignmentsViewModelTests.cs
```

---

## Platform-Specific Build Instructions

### Building for Windows (WinUI 3)

1. In Visual Studio, set the target platform to `Windows Machine` in the toolbar.
2. Go to Project Properties > Application > Package Manifest.
3. Ensure `TargetDeviceFamily` is set to `Windows.Universal`.
4. Press F5 to run, or use Build > Publish > Create App Packages for distribution.
5. Windows builds use the DPAPI for SecureStorage automatically.

### Building for Android

1. Ensure Android SDK tools are installed via Visual Studio Installer.
2. Connect a physical Android device with USB debugging enabled, or start an emulator.
3. Select your device from the target dropdown in Visual Studio.
4. Press F5 to deploy and run.
5. For a release APK: Build > Archive > Distribute > Ad Hoc or Google Play.

### Building for iOS

1. Requires a Mac with Xcode 15 or later connected via Mac Build Host, or
   use a Mac directly with Visual Studio for Mac.
2. In Visual Studio on Windows: Tools > iOS > Pair to Mac, and connect.
3. Select an iOS device or simulator from the target dropdown.
4. Press F5 to build and deploy.
5. For App Store distribution, configure provisioning profiles in Xcode on the Mac.

### Building for macOS (Mac Catalyst)

1. Open the solution on a Mac running macOS 13 or later.
2. Select the `EduAutomation (macOS)` target.
3. Press Cmd+R to run, or use the Publish workflow for Mac App Store submission.

---

## Running Unit Tests

1. Open the Test Explorer: Test > Test Explorer.
2. Click "Run All Tests" to execute all unit tests.
3. All tests use mocked service interfaces so no real API keys are needed.
4. To run from the terminal:

```
dotnet test EduAutomation.Tests/EduAutomation.Tests.csproj --verbosity normal
```

Test coverage targets:
- All service classes: greater than 85 percent branch coverage
- All ViewModels: greater than 90 percent line coverage
- All approval workflow paths: 100 percent coverage (critical safety requirement)

---

## Debugging Guide

### Enable Debug Logging

The app writes timestamped structured logs to two sinks simultaneously:

1. The Visual Studio Output window (Debug sink).
2. A rolling file at the platform's local app data path:
   - Windows: `%LOCALAPPDATA%\EduAutomation\logs\log-.txt`
   - Android: `/data/data/com.eduautomation/files/logs/log-.txt`
   - iOS/macOS: `~/Library/Application Support/EduAutomation/logs/log-.txt`

All log entries follow this format:

```
[2025-01-15 14:23:07.123 INF] [CanvasService.GetMissingAssignmentsAsync] Successfully fetched 3 missing assignments for course MATH-101
[2025-01-15 14:23:08.456 ERR] [CanvasService.GetMissingAssignmentsAsync] HTTP 429 Rate Limit - Retry attempt 1 of 3 after 2000ms delay
```

### Inspecting API Responses

Set the environment variable `EDUAUTO_LOG_RAW_RESPONSES=true` during development
to log full raw JSON API responses. NEVER enable this in production as it may
log sensitive student data.

### Breakpoints for Critical Paths

Place breakpoints at these locations for the most productive debugging sessions:

- `ReviewViewModel.ApproveAndSubmitAsync` - verify the approval gate fires correctly
- `OpenAIService.GenerateAssignmentResponseAsync` - inspect GPT-4 prompt and response
- `CanvasService.SubmitAssignmentAsync` - confirm submission payload before it sends
- `TokenRefreshHandler.SendAsync` - watch token refresh lifecycle

---

## API Rate Limit and Token Expiration Handling

### Canvas API

Canvas enforces a rate limit of 700 requests per 10 minutes per user token.
The `ApiRateLimitHandler` uses Polly with exponential backoff:

- On HTTP 429: waits for the duration specified in the `X-Rate-Limit-Remaining`
  and `X-Request-Cost` headers, then retries up to 3 times.
- On HTTP 401: triggers `TokenRefreshHandler` which prompts the user to
  re-enter their Canvas token via the Settings page.

### Google Gmail API

Gmail uses OAuth 2.0. The `Google.Apis.Auth` library automatically refreshes
the access token using the stored refresh token before expiration. The app checks
token validity before every API call and preemptively refreshes if the token
expires within 5 minutes. On `TokenExpiredException`, the app clears the stored
credential and restarts the OAuth flow.

### OpenAI API

- On HTTP 429 (rate limit): Polly retries with exponential backoff up to 5 times,
  starting at a 1-second delay and doubling each attempt.
- On HTTP 503 (service unavailable): Polly circuit breaker opens after 3 consecutive
  failures and stays open for 30 seconds before testing again.
- Token expiration does not apply (API key-based authentication). If HTTP 401 is
  received, the user is prompted to re-enter their OpenAI key in Settings.

### Infinite Campus

- Session cookies expire after approximately 30 minutes of inactivity.
- The `InfiniteCampusService` tracks the session creation timestamp and
  proactively re-authenticates when the session is 25 minutes old.
- On `HttpRequestException` during scraping, the service logs the full HTML
  response for debugging and throws a meaningful `ServiceUnavailableException`.

---

## Troubleshooting Common Issues

### "Google OAuth redirect URI mismatch" error

- Ensure the OAuth client in Google Cloud Console is set to type "Desktop App."
- The redirect URI for desktop apps must be `http://localhost` with a dynamic port.
  The Google.Apis.Auth library handles this automatically for desktop app clients.

### "Canvas API returns empty assignment list"

- Verify the Canvas base URL includes no trailing slash.
- Confirm the API token has not expired in Canvas Settings > Approved Integrations.
- Check that the API call uses the correct enrollment state filter (`active`).

### "OpenAI model not found" error

- The model ID used is `gpt-4o`. If your account does not have GPT-4 access,
  change the `ModelId` constant in `OpenAIService.cs` to `gpt-3.5-turbo`
  as a fallback (output quality will be reduced).

### App crashes on first launch (Android)

- Ensure the Android target SDK is set to API 34 in the project file.
- The SecureStorage on Android requires the app to be signed. Run in Release
  mode or configure a debug signing certificate in the project properties.

### MAUI Hot Reload not reflecting XAML changes

- Press Ctrl+Shift+F5 to force a full hot restart instead of hot reload.

---

## Security Notes

- The `google_credentials.json` file contains OAuth client secrets. Add it to
  `.gitignore` immediately. It is included in the `.gitignore` template below.
- Never log API tokens or passwords. The `LoggingService` has a built-in
  sanitizer that replaces any string matching an API key pattern with `[REDACTED]`.
- The assignment submission path has an immovable guard clause:
  `if (!item.IsApprovedByUser) throw new UnauthorizedSubmissionException(...)`.
  This cannot be bypassed without modifying source code.

### Recommended .gitignore additions

```
# API Credentials
**/Resources/Raw/google_credentials.json
**/*.user.json
**/appsettings.Development.json

# Secrets
**/.env
**/secrets.json

# Build outputs
**/bin/
**/obj/
**/.vs/
```
#   E D U - A u t o m a t i o n 
 
 