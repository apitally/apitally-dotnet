.PHONY: format check test test-coverage test-matrix

FRAMEWORK ?= net10.0

format:
	dotnet csharpier format .

check:
	dotnet csharpier check .

test:
	dotnet test --framework $(FRAMEWORK) --logger "console;verbosity=normal"

test-coverage:
	rm -rf tests/Apitally.Tests/TestResults
	dotnet test --framework $(FRAMEWORK) --logger "console;verbosity=normal" --collect:"XPlat Code Coverage"

test-matrix:
	dotnet test --logger "console;verbosity=normal"
