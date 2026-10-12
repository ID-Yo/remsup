import sys
from importlib.metadata import PackageNotFoundError, version
try:
    valid = sys.version_info >= (3, 11) and version("mcp") == "1.27.0"
except PackageNotFoundError:
    valid = False
sys.exit(0 if valid else 1)
