from mcp.server.fastmcp import FastMCP
import docker
import os
import tempfile
import shutil
import argparse
import pathlib
import logging
import time
import atexit
import threading

# Create MCP server
mcp = FastMCP("Docker Python Execution")
client = docker.from_env()
code_dir = None
image_name = None

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

# Container management
class ContainerManager:
    def __init__(self, max_idle_time=600, max_containers=10):  # 600 seconds = 10 minutes
        self.containers = {}  # Map of container_id -> {'container': container_obj, 'last_used': timestamp, 'busy': bool}
        self.max_idle_time = max_idle_time
        self.max_containers = max_containers
        self.lock = threading.RLock()
        self.cleanup_thread = None
        self.running = True
    
    def start_cleanup_thread(self):
        """Start a background thread to periodically clean up idle containers"""
        self.cleanup_thread = threading.Thread(target=self._cleanup_loop, daemon=True)
        self.cleanup_thread.start()
        logging.info("Container cleanup thread started")
    
    def _cleanup_loop(self):
        """Background thread that periodically cleans up idle containers"""
        while self.running:
            time.sleep(60)  # Check every minute
            self.cleanup_idle_containers()
    
    def cleanup_idle_containers(self):
        """Remove containers that have been idle for too long"""
        now = time.time()
        to_remove = []
        
        with self.lock:
            # Identify containers to remove
            for container_id, info in self.containers.items():
                # Only remove non-busy containers
                if not info['busy'] and now - info['last_used'] > self.max_idle_time:
                    to_remove.append(container_id)
            
            # Remove identified containers
            for container_id in to_remove:
                try:
                    container = self.containers[container_id]['container']
                    logging.info(f"Removing idle container {container_id} (idle for {now - self.containers[container_id]['last_used']:.1f} seconds)")
                    container.remove(force=True)
                    del self.containers[container_id]
                except Exception as e:
                    logging.warning(f"Error removing container {container_id}: {e}")
    
    def add_container(self, container):
        """Add a container to be managed"""
        with self.lock:
            self.containers[container.id] = {
                'container': container,
                'last_used': time.time(),
                'busy': False  # Initially not busy
            }
            logging.info(f"Added container {container.id} to manager")
    
    def get_container_count(self):
        """Get the number of containers being managed"""
        with self.lock:
            return len(self.containers)
    
    def get_available_container(self):
        """Get an available container for reuse or None if none available"""
        with self.lock:
            for container_id, info in self.containers.items():
                if not info['busy']:
                    # Mark as busy and return
                    info['busy'] = True
                    info['last_used'] = time.time()  # Update last used time
                    logging.info(f"Reusing existing container {container_id}")
                    return info['container']
            return None
    
    def mark_container_as_available(self, container_id):
        """Mark a container as no longer busy"""
        with self.lock:
            if container_id in self.containers:
                self.containers[container_id]['busy'] = False
                self.containers[container_id]['last_used'] = time.time()
                logging.info(f"Container {container_id} marked as available for reuse")
    
    def update_last_used(self, container_id):
        """Update the last used timestamp for a container"""
        with self.lock:
            if container_id in self.containers:
                self.containers[container_id]['last_used'] = time.time()
    
    def should_create_new_container(self):
        """Check if we should create a new container based on current count"""
        with self.lock:
            # If we have fewer containers than max_containers, we can create a new one
            return len(self.containers) < self.max_containers
    
    def cleanup_all(self):
        """Remove all containers being managed"""
        self.running = False
        with self.lock:
            for container_id, info in list(self.containers.items()):
                try:
                    container = info['container']
                    logging.info(f"Removing container {container_id} during cleanup")
                    container.remove(force=True)
                except Exception as e:
                    logging.warning(f"Error removing container {container_id} during cleanup: {e}")
            self.containers.clear()
        logging.info("All containers cleaned up")

# Create a container manager
container_manager = ContainerManager()

# Register the cleanup function to run when the server exits
def cleanup_on_exit():
    logging.info("Shutting down server, cleaning up resources...")
    # Stop the cleanup thread
    container_manager.running = False
    if container_manager.cleanup_thread and container_manager.cleanup_thread.is_alive():
        container_manager.cleanup_thread.join(timeout=5)
    # Clean up all containers
    container_manager.cleanup_all()
    logging.info("Cleanup complete")

atexit.register(cleanup_on_exit)

def main():
    """Entry point for the MCP server"""
    global image_name, code_dir
    parser = argparse.ArgumentParser(description='Start MCP server with Docker Python execution')
    parser.add_argument('--code-dir', type=str,
                        help='Directory path where code will be stored')
    parser.add_argument('--image', type=str, default='pyexec:latest',
                        help='Docker image to use for Python execution')
    parser.add_argument('--idle-timeout', type=int, default=600,
                        help='Time in seconds before idle containers are removed (default: 600)')
    parser.add_argument('--max-containers', type=int, default=10,
                        help='Maximum number of containers to keep in the pool (default: 10)')
    args = parser.parse_args()

    # Ensure the code directory exists
    code_dir = args.code_dir
    if not code_dir:
        # Use a temp directory if no code directory is specified
        code_dir = os.path.join(tempfile.gettempdir(), "python_code_execution")

    image_name = args.image
    os.makedirs(code_dir, exist_ok=True)
    
    # Configure container manager
    container_manager.max_idle_time = args.idle_timeout
    container_manager.max_containers = args.max_containers
    container_manager.start_cleanup_thread()
    logging.info(f"Container idle timeout set to {args.idle_timeout} seconds, max containers: {args.max_containers}")

    # Create and run the server
    mcp.run()

def _sanitize_path(relative_path):
    """Helper to sanitize and validate a relative path"""
    # Convert to Path object to handle different path formats
    path_obj = pathlib.Path(relative_path)
    
    # Ensure the path is relative and doesn't try to escape
    if path_obj.is_absolute():
        raise ValueError("Path must be relative, not absolute")
        
    if ".." in path_obj.parts:
        raise ValueError("Path cannot contain '..' to navigate up directories")
    
    # Get the absolute path and resolve any symlinks
    code_dir_path = pathlib.Path(code_dir).resolve()
    target_path = (code_dir_path / path_obj).resolve()
    
    # Ensure the resolved path is still within code_dir
    if code_dir_path not in target_path.parents and target_path != code_dir_path:
        raise ValueError("Path must stay within the code directory")
    
    return str(target_path)

@mcp.tool()
def execute_python_in_container(code: str) -> str:
    """
    Execute Python code in a Docker container. The environment is limited to the container.
    Following packages are available:
    "pandas", "numpy", "matplotlib", "seaborn", "plotly", "bokeh", "hvplot",
    "datashader", "plotnine", "cufflinks", "graphviz", "scipy", "statsmodels",
    "openpyxl", "xlrd", "xlsxwriter", "pandasql", "csv23", "csvkit", "polars",
    "pyarrow", "fastparquet", "dask", "vaex", "python-dateutil", "elasticsearch",
    "psycopg2-binary", "beautifulsoup4", "requests", "lxml", "geopandas",
    "folium", "pydeck", "holoviews", "altair", "visualkeras", "kaleido",
    "panel", "voila", "pymongo"

    Use following environment variables (if the connection string is available):
    - MONGO_URI: MongoDB connection string
    - ELASTIC_URI: ElasticSearch connection string
    - REDIS_URI: Redis connection string
    - POSTGRES_URI: PostgreSQL connection string
    - MYSQL_URI: MySQL connection string
    
    Args:
        code: Python code to execute

    Returns:
        Output from executed code
    """
    script_dir = None
    try:
        # Use the code directory directly since we're reusing containers
        script_dir = code_dir
        os.makedirs(script_dir, exist_ok=True)
        
        script_path = os.path.join(script_dir, "script.py")
        
        with open(script_path, "w") as f:
            f.write(code)
        
        # Filter out system environment variables
        system_env_prefixes = [
            "PATH", "TEMP", "TMP", "HOME", "USER", 
            "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
            "SYSTEM", "PUBLIC", "COMPUTER", "OS", 
            "PROCESSOR", "PROGRAM", "WIN", "COMMON", 
            "PATH" # Listed twice deliberately as it's critical to exclude
        ]
        
        # Get non-system environment variables
        env_vars = {}
        for key, value in os.environ.items():
            # Skip variables that start with system prefixes
            if not any(key.upper().startswith(prefix) for prefix in system_env_prefixes):
                env_vars[key] = value
                
        logging.info(f"Passing {len(env_vars)} environment variables to container")
        
        # First, try to get an available container for reuse
        container = container_manager.get_available_container()
        reusing_container = container is not None
        
        if not container and container_manager.should_create_new_container():
            # No available containers, create a new one
            container = client.containers.run(
                image=image_name,
                command=["sleep", "3600"],  # Initially just sleep, we'll execute code later
                volumes={script_dir: {'bind': '/code', 'mode': 'rw'}},
                detach=True,
                remove=False,  # Set to False to prevent automatic removal
                mem_limit="512m",
                cap_drop=["ALL"],
                security_opt=["no-new-privileges"],
                environment=env_vars,
                network="none",
                auto_remove=True
            )
            
            # Add the container to the manager for tracking
            container_manager.add_container(container)
            logging.info(f"Created new container {container.id} (total active: {container_manager.get_container_count()})")
        elif not container:
            # Hit the max container limit, wait for an available container
            logging.info("Maximum container limit reached, waiting for an available container...")
            max_wait_time = 30  # Maximum time to wait in seconds
            wait_interval = 0.5  # Check every half second
            waited_time = 0
            
            while waited_time < max_wait_time:
                container = container_manager.get_available_container()
                if container:
                    reusing_container = True
                    break
                time.sleep(wait_interval)
                waited_time += wait_interval
            
            if not container:
                return "Error: Maximum container limit reached and no containers became available in time"
        
        try:
            if reusing_container:
                # For existing containers, we need to execute the script directly
                logging.info(f"Executing code in existing container {container.id}")
                pass

            # Execute the script in the container
            exec_result = container.exec_run(
                cmd=["python", "/code/script.py"],
                environment=env_vars
            )
            
            # Mark the container as available for reuse
            container_manager.mark_container_as_available(container.id)
            
            # Check if execution was successful
            if exec_result.exit_code != 0:
                return f"Error executing code (exit code {exec_result.exit_code}):\n{exec_result.output.decode('utf-8')}"
                
            # Clean up - just delete the script file, not the directory
            if os.path.exists(script_path):
                try:
                    os.remove(script_path)
                except Exception as e:
                    logging.warning(f"Error deleting script file: {str(e)}")
            
            # Return the successful result
            return exec_result.output.decode('utf-8')
            
        except Exception as e:
            # If there's an error, mark the container as available
            # If it's a serious error with the container itself, it will be cleaned up separately
            try:
                container_manager.mark_container_as_available(container.id)
            except Exception as mark_error:
                logging.warning(f"Failed to mark container as available after error: {mark_error}")
                
            # Clean up script file on error too
            if os.path.exists(script_path):
                try:
                    os.remove(script_path)
                except Exception as clean_error:
                    logging.warning(f"Error deleting script file after error: {str(clean_error)}")
                    
            # Return error message
            return f"Error executing code: {str(e)}"
    except Exception as e:
        # Clean up even if there's an error
        if script_path and os.path.exists(script_path):
            try:
                os.remove(script_path)
                logging.info(f"Deleted script file after error: {script_path}")
            except Exception as clean_error:
                logging.warning(f"Error deleting script file after main error: {str(clean_error)}")
            
        return f"Error executing code: {str(e)}"

# @mcp.tool()
def list_directory(relative_path: str = "") -> str:
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

# @mcp.tool()
def read_file(relative_path: str) -> str:
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

# @mcp.tool()
def write_file(relative_path: str, content: str) -> str:
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

# @mcp.tool()
def delete_file(relative_path: str) -> str:
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

# @mcp.tool()
def get_directory_tree(relative_path: str = "") -> str:
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

# @mcp.tool()
def cleanup_code_directory() -> str:
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