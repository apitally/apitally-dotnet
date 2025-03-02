.PHONY: format test test-coverage test-matrix

format:
	dotnet csharpier .

test:
	dotnet test --framework net9.0

test-coverage:
	dotnet test --framework net9.0 --collect:"XPlat Code Coverage"

test-matrix:
	dotnet test
