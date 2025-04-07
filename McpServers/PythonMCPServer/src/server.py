from mcp.server.fastmcp import FastMCP
import docker
import os
import tempfile
import shutil
import argparse
import pathlib
import logging

# Create MCP server
mcp = FastMCP("Docker Python Execution")
client = docker.from_env()
code_dir = None
image_name = None

def main():
    """Entry point for the MCP server"""
    global image_name, code_dir
    parser = argparse.ArgumentParser(description='Start MCP server with Docker Python execution')
    parser.add_argument('--code-dir', type=str,
                        help='Directory path where code will be stored')
    parser.add_argument('--image', type=str, default='python:3.9-slim',
                        help='Docker image to use for Python execution')
    args = parser.parse_args()

    # Ensure the code directory exists
    code_dir = args.code_dir
    if not code_dir:
        # Use a temp directory if no code directory is specified
        code_dir = os.path.join(tempfile.gettempdir(), "python_code_execution")

    image_name = args.image
    os.makedirs(code_dir, exist_ok=True)
    print(f"Using code directory: {code_dir}")

    # Create and run the server
    mcp.run()

def _sanitize_path(relative_path):
    """Helper to sanitize and validate a relative path"""
    # Convert to Path object to handle different path formats
    path_obj = pathlib.Path(relative_path)
    
    # Ensure the path doesn't try to escape the code directory
    if ".." in path_obj.parts:
        raise ValueError("Path cannot contain '..' to navigate up directories")
    
    # Get the absolute path
    absolute_path = os.path.join(code_dir, str(path_obj))
    
    # Ensure the absolute path is still within code_dir
    if not os.path.abspath(absolute_path).startswith(os.path.abspath(code_dir)):
        raise ValueError("Path must stay within the code directory")
    
    return absolute_path

@mcp.tool()
async def execute_python_in_container(code: str) -> str:
    """
    Execute Python code in a Docker container. The environment is limited to the container.
    Following packages are available:
    - pandas
    - numpy
    - matplotlib
    - seaborn
    - plotly
    - bokeh
    - hvplot
    - datashader
    - plotnine
    - cufflinks
    - graphviz
    - scipy
    - statsmodels
    - openpyxl
    - xlrd
    - xlsxwriter
    - pandasql
    - csv23
    - csvkit
    - polars
    - pyarrow
    - fastparquet
    - dask
    - vaex
    - python-dateutil
    - beautifulsoup4
    - requests
    - lxml
    - geopandas
    - folium
    - pydeck
    - holoviews
    - altair
    - visualkeras
    - kaleido
    - panel
    - voila
    
    Args:
        code: Python code to execute

    Returns:
        Output from executed code
    """
    script_dir = None
    try:
        # Create a unique subdirectory for this execution
        script_dir = os.path.join(code_dir, f"exec-{tempfile.mktemp(dir='').split('/')[-1]}")
        os.makedirs(script_dir, exist_ok=True)
        
        script_path = os.path.join(script_dir, "script.py")
        
        with open(script_path, "w") as f:
            f.write(code)
        
        # Run the container with the code
        container = client.containers.run(
            image=image_name,
            command=["python", "/code/script.py"],
            volumes={script_dir: {'bind': '/code', 'mode': 'ro'}},
            detach=True,
            remove=False,  # Set to False to prevent automatic removal
            network_mode="none",
            mem_limit="512m",
            cap_drop=["ALL"],
            security_opt=["no-new-privileges"]
        )
        
        try:
            # Wait for the container to complete naturally
            container.wait(timeout=30)
            # Get logs after the container has completed
            result = container.logs()
        finally:
            # Clean up the container after getting the logs
            try:
                container.remove()
            except Exception as e:
                logging.warning(f"Failed to remove container: {e}")
        
        # Clean up - explicitly delete the script file first, then the directory
        if os.path.exists(script_path):
            try:
                os.remove(script_path)
                print(f"Deleted script file: {script_path}")
            except Exception as e:
                print(f"Error deleting script file: {str(e)}")
        
        # Remove the directory after file deletion
        if os.path.exists(script_dir):
            shutil.rmtree(script_dir, ignore_errors=True)
            print(f"Deleted script directory: {script_dir}")
        
        return result.decode('utf-8')
    except Exception as e:
        # Clean up even if there's an error
        if script_path and os.path.exists(script_path):
            try:
                os.remove(script_path)
                print(f"Deleted script file after error: {script_path}")
            except Exception as clean_error:
                print(f"Error deleting script file after main error: {str(clean_error)}")
        
        # Remove the directory after file deletion
        if script_dir and os.path.exists(script_dir):
            shutil.rmtree(script_dir, ignore_errors=True)
            print(f"Deleted script directory after error: {script_dir}")
            
        return f"Error executing code: {str(e)}"

@mcp.tool()
async def list_directory(relative_path: str = "") -> str:
    """
    List the contents of a directory within the code directory where python code is executed
    
    Args:
        relative_path: Relative path within the code directory (default: list root code directory)
        
    Returns:
        Directory listing as a string
    """
    try:
        # Sanitize and get absolute path
        if not relative_path:
            dir_path = code_dir
        else:
            dir_path = _sanitize_path(relative_path)
        
        # Check if directory exists
        if not os.path.isdir(dir_path):
            return f"Error: Directory does not exist: {relative_path}"
        
        # List contents
        items = os.listdir(dir_path)
        
        if not items:
            return f"Directory is empty: {relative_path or '.'}"
        
        result = f"Contents of {relative_path or '.'} (total: {len(items)}):\n"
        
        # Separate directories and files
        dirs = []
        files = []
        
        for item in items:
            item_path = os.path.join(dir_path, item)
            if os.path.isdir(item_path):
                dirs.append(f"📁 {item}/")
            else:
                size = os.path.getsize(item_path)
                files.append(f"📄 {item} ({_format_size(size)})")
        
        # Sort and add to result
        for d in sorted(dirs):
            result += f"{d}\n"
        
        for f in sorted(files):
            result += f"{f}\n"
        
        return result
    except ValueError as e:
        return f"Error: {str(e)}"
    except Exception as e:
        return f"Error listing directory: {str(e)}"

def _format_size(size_bytes):
    """Format file size in human-readable format"""
    for unit in ['B', 'KB', 'MB', 'GB']:
        if size_bytes < 1024.0:
            return f"{size_bytes:.1f} {unit}"
        size_bytes /= 1024.0
    return f"{size_bytes:.1f} TB"

@mcp.tool()
async def read_file(relative_path: str) -> str:
    """
    Read a file from the code directory where python code is executed
    
    Args:
        relative_path: Relative path to the file within the code directory
        
    Returns:
        File contents as a string
    """
    try:
        # Sanitize and get absolute path
        file_path = _sanitize_path(relative_path)
        
        # Check if file exists
        if not os.path.isfile(file_path):
            return f"Error: File does not exist: {relative_path}"
        
        # Read file
        with open(file_path, 'r') as f:
            content = f.read()
        
        return f"Contents of {relative_path}:\n\n{content}"
    except ValueError as e:
        return f"Error: {str(e)}"
    except Exception as e:
        return f"Error reading file: {str(e)}"

@mcp.tool()
async def write_file(relative_path: str, content: str) -> str:
    """
    Write content to a file in the code directory where python code is executed
    
    Args:
        relative_path: Relative path to the file within the code directory
        content: Content to write to the file
        
    Returns:
        Status message
    """
    try:
        # Sanitize and get absolute path
        file_path = _sanitize_path(relative_path)
        
        # Make sure the directory exists
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        
        # Write file
        with open(file_path, 'w') as f:
            f.write(content)
        
        return f"Successfully wrote {len(content)} bytes to {relative_path}"
    except ValueError as e:
        return f"Error: {str(e)}"
    except Exception as e:
        return f"Error writing file: {str(e)}"

@mcp.tool()
async def delete_file(relative_path: str) -> str:
    """
    Delete a file from the code directory where python code is executed
    
    Args:
        relative_path: Relative path to the file within the code directory
        
    Returns:
        Status message
    """
    try:
        # Sanitize and get absolute path
        file_path = _sanitize_path(relative_path)
        
        # Check if file exists
        if not os.path.isfile(file_path):
            return f"Error: File does not exist: {relative_path}"
        
        # Delete file
        os.remove(file_path)
        
        return f"Successfully deleted file: {relative_path}"
    except ValueError as e:
        return f"Error: {str(e)}"
    except Exception as e:
        return f"Error deleting file: {str(e)}"

@mcp.tool()
async def get_directory_tree(relative_path: str = "") -> str:
    """
    Get an ASCII tree representation of a directory structure where python code is executed
    
    Args:
        relative_path: Relative path within the code directory (default: root code directory)
        
    Returns:
        ASCII tree representation as a string
    """
    try:
        # Sanitize and get absolute path
        if not relative_path:
            dir_path = code_dir
            display_path = "."
        else:
            dir_path = _sanitize_path(relative_path)
            display_path = relative_path
        
        # Check if directory exists
        if not os.path.isdir(dir_path):
            return f"Error: Directory does not exist: {relative_path}"
        
        # Generate the tree
        result = f"{display_path}\n"
        result += _generate_tree(dir_path, code_dir, prefix="")
        
        return result
    except ValueError as e:
        return f"Error: {str(e)}"
    except Exception as e:
        return f"Error generating directory tree: {str(e)}"

def _generate_tree(path, base_path, prefix=""):
    """Recursively generate tree structure"""
    if not os.path.isdir(path):
        return ""
    
    result = ""
    items = sorted(os.listdir(path))
    
    # Get relative path for display
    rel_path = os.path.relpath(path, base_path)
    if rel_path == ".":
        rel_path = ""
    
    for i, item in enumerate(items):
        item_path = os.path.join(path, item)
        is_last = i == len(items) - 1
        
        # Add item to result
        result += f"{prefix}{'└── ' if is_last else '├── '}{item}"
        
        if os.path.isdir(item_path):
            result += "/\n"
            # Update prefix for children
            new_prefix = prefix + ('    ' if is_last else '│   ')
            result += _generate_tree(item_path, base_path, new_prefix)
        else:
            size = os.path.getsize(item_path)
            result += f" ({_format_size(size)})\n"
    
    return result

@mcp.tool()
async def cleanup_code_directory() -> str:
    """
    Clean up the code directory by removing all files and subdirectories
    
    Returns:
        Status message
    """
    try:
        for item in os.listdir(code_dir):
            item_path = os.path.join(code_dir, item)
            if os.path.isdir(item_path):
                shutil.rmtree(item_path, ignore_errors=True)
            else:
                os.remove(item_path)
        
        return f"Successfully cleaned up code directory: {code_dir}"
    except Exception as e:
        return f"Error cleaning up code directory: {str(e)}"

if __name__ == "__main__":
    main()