"""Docker Python Execution MCP server package."""

__version__ = "0.1.0"

# Optional: Import and expose main components
from .server import main

__all__ = ["main", "__version__"]