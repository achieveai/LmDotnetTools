# Code Formatting Setup for LmDotnetTools

This repository uses **CSharpier** for consistent C# code formatting across the codebase. CSharpier is a Prettier-inspired formatter that provides deterministic, opinionated formatting with proper line wrapping.

## 🎯 Quick Start

### Installation
```bash
# Install CSharpier as global dotnet tool (one-time setup)
dotnet tool install csharpier --global
```

### Format All Code
```bash
# Apply formatting to all files
csharpier format .

# Check if files are formatted (CI/CD usage)
csharpier check .

# Format specific file or directory
csharpier format src/LmCore/
csharpier format src/LmCore/Messages/TextMessage.cs
```

## ⚙️ Configuration

### CSharpier Settings (`.csharpierrc.json`)
```json
{
  "printWidth": 100,
  "useTabs": false,
  "endOfLine": "crlf"
}
```

### Per-File-Type Indentation (`.editorconfig`)
CSharpier respects `.editorconfig` for file-specific indentation:
```ini
# C# files: 4 spaces
[*.{cs,csx}]
indent_size = 4

# XML/Project files: 2 spaces  
[*.{csproj,props,targets,xml,config,xaml}]
indent_size = 2

# JSON files: 2 spaces
[*.{json,json5}]
indent_size = 2
```

### Key Features
- **Print Width**: 100 characters (hard wrapping)
- **Indentation**: Per-file-type (C#: 4 spaces, XML/JSON: 2 spaces)
- **Line Endings**: CRLF (Windows standard)
- **Deterministic**: Same input always produces same output
- **Opinionated**: Minimal configuration needed

### What CSharpier Formats
- **Indentation and spacing**: Consistent throughout codebase
- **Line wrapping**: Automatic at 100 characters
- **Braces and brackets**: Consistent placement
- **Using statements**: Proper ordering and grouping
- **Method signatures**: Multi-line when needed
- **Object initializers**: Proper formatting

## 🔧 IDE Integration

### Visual Studio
1. Install the **CSharpier** extension from Visual Studio Marketplace
2. Enable format on save:
   - **Tools** → **Options** → **Text Editor** → **C#** → **Code Style** → **Formatting**
   - Check "Format document on save"

### VS Code
1. Install the **CSharpier** extension
2. Enable format on save in `settings.json`:
```json
{
    "editor.formatOnSave": true,
    "editor.defaultFormatter": "csharpier.csharpier-vscode"
}
```

### JetBrains Rider
1. Install the **CSharpier** plugin
2. Configure in **Settings** → **Tools** → **CSharpier**
3. Enable "Reformat with CSharpier on Save"

## 🚀 CI/CD Integration

### GitHub Actions
```yaml
name: Code Formatting Check

on: [push, pull_request]

jobs:
  format-check:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '9.0.x'
    - name: Install CSharpier
      run: dotnet tool install csharpier --global
    - name: Check code formatting
      run: csharpier check .
```

### Azure DevOps
```yaml
- task: DotNetCoreCLI@2
  displayName: 'Install CSharpier'
  inputs:
    command: 'custom'
    custom: 'tool'
    arguments: 'install csharpier --global'

- script: csharpier check .
  displayName: 'Check code formatting'
```

## 🛠️ Troubleshooting

### Common Issues

**CSharpier not installed**
```bash
# Install globally
dotnet tool install csharpier --global

# Or install locally per project
dotnet new tool-manifest  # if no manifest exists
dotnet tool install csharpier --local
```

**Files not formatting**
- Ensure you're in the right directory
- Check `.csharpierrc` configuration is valid JSON
- Use `csharpier check .` to see what needs formatting

**IDE integration not working**
- Restart your IDE after installing extensions
- Check extension settings and enable format-on-save
- Verify CSharpier is installed globally

## 📁 File Coverage

CSharpier formats:
- **C# files** (*.cs): Complete formatting including line wrapping
- **Project files** (*.csproj, *.props): XML indentation and structure  
- **JSON files** (*.json): Proper indentation and structure

## 🎯 Why CSharpier?

### Advantages over `dotnet format`
- **No crashes**: CSharpier is stable and reliable
- **Hard line wrapping**: Enforces 100-character limit
- **Faster**: Typically 3x faster than `dotnet format`
- **Deterministic**: Same result every time
- **Less configuration**: Opinionated choices reduce bikeshedding

### Performance
- **518 files formatted in ~3 seconds**
- **Multi-threaded processing**
- **Incremental formatting** (only changed files)

## 🔄 Alternative: ReSharper Command Line Tools

### Installation
```bash
# Install ReSharper CLT as global dotnet tool
dotnet tool install -g JetBrains.ReSharper.GlobalTools
```

### Usage
```bash
# Format entire solution
jb cleanupcode LmDotnetTools.sln

# Format specific project
jb cleanupcode src/LmCore/AchieveAi.LmDotnetTools.LmCore.csproj

# Format with specific profile
jb cleanupcode --profile="Built-in: Reformat Code" LmDotnetTools.sln

# Check available options
jb cleanupcode --help
```

### Key Differences from CSharpier
- **Closing parentheses**: Stay on same line as last parameter
- **More configurable**: Custom profiles and settings
- **Slower**: Takes longer than CSharpier but more control
- **Uses .editorconfig**: Respects existing configuration

### CI/CD Integration
```yaml
# GitHub Actions
- name: Install ReSharper CLT
  run: dotnet tool install -g JetBrains.ReSharper.GlobalTools
- name: Format code
  run: jb cleanupcode LmDotnetTools.sln
```

### When to Consider ReSharper CLT
- Need specific formatting control (e.g., closing parenthesis placement)
- Already using JetBrains tools in your workflow
- Want more granular configuration options
- Performance is less critical than formatting precision

## 📚 References

- [CSharpier Official Documentation](https://csharpier.com/)
- [CSharpier GitHub Repository](https://github.com/belav/csharpier)
- [CSharpier Configuration Options](https://csharpier.com/docs/Configuration)
- [CSharpier IDE Extensions](https://csharpier.com/docs/Editors)
- [ReSharper Command Line Tools](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html)
- [JetBrains CleanupCode Documentation](https://www.jetbrains.com/help/resharper/CleanupCode.html)