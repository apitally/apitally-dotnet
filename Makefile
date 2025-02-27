.PHONY: format test test-coverage

format:
	dotnet csharpier .

test:
	dotnet test

test-coverage:
	dotnet test --collect:"XPlat Code Coverage"
