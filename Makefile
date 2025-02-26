.PHONY: test test-coverage

test:
	dotnet test

test-coverage:
	dotnet test --collect:"XPlat Code Coverage"
