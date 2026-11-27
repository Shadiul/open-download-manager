@echo off
rem Chrome on Windows needs an .exe or .bat as the host. This starts the Python test host.
python "%~dp0test_host.py" %*
