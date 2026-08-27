# Python >=3.10 is a hard floor for this requirements set, not a preference:
# bokeh==3.8.2 and geopandas==1.1.2 (both already pinned before this change) publish no
# cp39 distribution at all, so `pip install -r requirements.txt` fails outright on
# python:3.9-slim. requests==2.33.0, the first release carrying the extract_zipped_paths
# fix, also declares Requires-Python >=3.10. 3.10 is the lowest tag that satisfies all three
# while keeping wheels available for the older numpy/pandas/scipy pins below it.
FROM python:3.10-slim

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