FROM python:3.9-slim

# Create a non-privileged user
RUN useradd --create-home appuser
WORKDIR /home/appuser
USER appuser

# Set up a virtual environment
RUN python -m venv /home/appuser/venv
ENV PATH="/home/appuser/venv/bin:$PATH"

# Install the MCP SDK and Docker SDK
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy your MCP server code
COPY --chown=appuser:appuser . .

# Expose the port if needed
EXPOSE 8000

# Run the server
CMD ["python", "server.py"]