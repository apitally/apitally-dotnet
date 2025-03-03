.PHONY: format test test-coverage test-matrix

format:
	dotnet csharpier .

test:
	dotnet test --framework net9.0 --logger "console;verbosity=normal"

test-coverage:
	rm -rf tests/Apitally.Tests/TestResults
	dotnet test --framework net9.0 --logger "console;verbosity=normal" --collect:"XPlat Code Coverage"

test-matrix:
	dotnet test --logger "console;verbosity=normal"
