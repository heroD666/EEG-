@echo off
REM 将项目根目录及 materials/ 子目录下所有 .md 文档导出为 PDF
REM 依赖（一次性）: npm install -g md-to-pdf
REM PDF 样式配置: md2pdf.config.json

cd /d "%~dp0"

for /r %%f in (*.md) do (
    echo 导出 %%~nxf ...
    call md-to-pdf "%%f" --config-file md2pdf.config.json --basedir "%CD%"
)

echo.
echo 完成。PDF 文件与对应 .md 位于相同目录。
pause
