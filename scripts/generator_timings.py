"""Read Roslyn's /reportanalyzer generator timings without summing analyzer work.

Analyzer actions overlap C# compilation and each other, so their reported elapsed
times are deliberately not subtracted from the complete compiler measurement.
"""
import re


def read_generator_timings(contents):
    # Roslyn formats numbers using the current culture, even with English messages.
    number = r"\d+[.,]\d+"
    total = re.search(r"Total generator execution time: (" + number + r") seconds\.", contents)
    xamlg = re.search(r"^\s*(" + number + r")\s+\S+\s+XamlG\.Generator\.XamlIncrementalGenerator\s*$",
                      contents, re.MULTILINE)
    def seconds(match):
        return float(match[1].replace(",", ".")) if match else None
    return {"all_generators_seconds": seconds(total), "xamlg_seconds": seconds(xamlg)}
