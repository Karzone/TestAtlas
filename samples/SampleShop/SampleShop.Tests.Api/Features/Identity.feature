Feature: Identity API
    Signing in and reading the current user's profile through the REST API.

    Scenario: Sign in and read profile
        Given the shop API is available
        When the user "ada@example.com" signs in
        Then the current user's profile can be read
