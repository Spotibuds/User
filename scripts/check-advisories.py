import json
import sys
from pathlib import Path


def verify(report, expected):
    assert report.get("version") == 1, "Incomplete advisory report version"
    projects = report.get("projects")
    assert isinstance(projects, list) and projects, "Missing advisory projects"
    names = [Path(project.get("path", "").replace("\\", "/")).name for project in projects]
    assert len(names) == len(set(names)) and set(names) == set(expected), "Unexpected advisory projects"

    def errors(value):
        if isinstance(value, dict):
            for key, item in value.items():
                assert not (key in {"problems", "errors", "error"} and item), "Advisory lookup failed"
                errors(item)
        elif isinstance(value, list):
            for item in value:
                errors(item)

    errors(report)
    for project in projects:
        frameworks = project.get("frameworks", [])
        assert isinstance(frameworks, list), "Invalid advisory frameworks"
        for framework in frameworks:
            assert not framework.get("topLevelPackages") and not framework.get("transitivePackages"), "Vulnerable dependencies"


def self_test():
    clean = {"version": 1, "projects": [{"path": "/example/App.csproj"}]}
    verify(clean, ["App.csproj"])
    invalid = [
        {},
        {"version": 1, "projects": []},
        {"version": 2, "projects": clean["projects"]},
        {"version": 1, "projects": [{"path": "/example/Other.csproj"}]},
        {"version": 1, "projects": clean["projects"] * 2},
        {**clean, "problems": [{"message": "unavailable"}]},
        {"version": 1, "projects": [{"path": "/example/App.csproj", "frameworks": [{"transitivePackages": [{"id": "bad"}]}]}]},
        {"version": 1, "projects": [{"path": "/example/App.csproj", "frameworks": {"errors": ["failed"]}}]},
    ]
    for report in invalid:
        try:
            verify(report, ["App.csproj"])
        except (AssertionError, AttributeError):
            continue
        raise AssertionError("Invalid advisory report accepted")
    print("Advisory gate regressions passed: one complete clear report and eight rejected invalid reports.")


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["--self-test"]:
            self_test()
        else:
            assert len(sys.argv) >= 3, "Provide report path and all expected project filenames"
            verify(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig")), sys.argv[2:])
            print("Advisory report complete; zero known vulnerable dependencies.")
    except (AssertionError, AttributeError, KeyError, TypeError, ValueError, OSError):
        print("Dependency advisory gate failed: incomplete, unavailable, or vulnerable report.", file=sys.stderr)
        sys.exit(1)
