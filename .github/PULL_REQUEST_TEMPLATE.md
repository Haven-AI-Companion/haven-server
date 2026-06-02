## Description
Please include a summary of the changes and the related issue/motivation. Include details on what problem is solved, what features are added, and how to verify.

Fixes # (issue) / Closes # (issue)

## Type of Change
Please delete options that are not relevant:
- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Security hardening / Vulnerability fix
- [ ] Refactoring / Code cleanup
- [ ] Documentation update

## How Has This Been Tested?
Please describe the tests that you ran to verify your changes. Provide instructions so we can reproduce. Please also list any relevant details for your test configuration.
- [ ] Verified local builds compile successfully with 0 errors/warnings (`dotnet build`)
- [ ] Executed automated integration test suite (`.\test_endpoints.ps1`) and confirmed all tests passed
- [ ] Manually tested UI changes / host bindings in dashboard (`admin.html` / `index.html`)

**Test Environment:**
- OS: [e.g., Windows 11, Linux, macOS]
- Backend Config: [e.g., Ollama fallback, OpenAI Compat, custom API]

## Checklist:
- [ ] My code follows the style guidelines of this project
- [ ] I have performed a self-review of my own code
- [ ] I have commented my code, particularly in hard-to-understand areas
- [ ] I have made corresponding changes to the documentation (e.g., README.md)
- [ ] My changes generate no new warnings or build errors
- [ ] I have not committed any personal configuration files, developer keys, database files, or secrets (validated against `.gitignore` settings)
