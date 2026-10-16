import subprocess, os, sys

os.chdir(r"C:\D\opt\win-desktop-helper")
csc = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

print("=== build test exe ===")
r = subprocess.run([csc, "-nologo", "-target:exe", "-out:annotate-geom-test.exe",
                    "-r:System.dll", "-r:System.Drawing.dll",
                    "shot-annotate-geom.cs", "shot-annotate-test.cs"],
                   capture_output=True, text=True, errors='replace')
print("returncode:", r.returncode)
print("STDOUT:", r.stdout)
print("STDERR:", r.stderr)
if r.returncode != 0:
    sys.exit(1)

print("=== run test exe ===")
r2 = subprocess.run(["annotate-geom-test.exe"], capture_output=True, text=True, errors='replace')
print("returncode:", r2.returncode)
print("STDOUT:", r2.stdout)
print("STDERR:", r2.stderr)
print("=== png exists? ===")
print(os.path.exists("annotate-geom-chart.png"), os.path.getsize("annotate-geom-chart.png") if os.path.exists("annotate-geom-chart.png") else 0)
