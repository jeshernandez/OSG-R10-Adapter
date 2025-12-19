#!/bin/bash
# Build script for OSG-R10-Adapter
# Builds self-contained executable

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Default parameters
RUNTIME="linux-x64"
CONFIGURATION="Release"
SELF_CONTAINED="true"
SINGLE_FILE="true"

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -r|--runtime)
            RUNTIME="$2"
            shift 2
            ;;
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --no-self-contained)
            SELF_CONTAINED="false"
            shift
            ;;
        --no-single-file)
            SINGLE_FILE="false"
            shift
            ;;
        -h|--help)
            echo "Usage: $0 [options]"
            echo ""
            echo "Options:"
            echo "  -r, --runtime <RID>           Runtime identifier (default: linux-x64)"
            echo "                                 Options: win-x64, win-arm64, linux-x64, linux-arm64"
            echo "  -c, --configuration <CONFIG>  Build configuration (default: Release)"
            echo "                                 Options: Debug, Release"
            echo "  --no-self-contained           Build as framework-dependent"
            echo "  --no-single-file              Don't package as single file"
            echo "  -h, --help                    Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            echo "Use -h or --help for usage information"
            exit 1
            ;;
    esac
done

echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN}OSG-R10-Adapter Build Script${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""
echo -e "${YELLOW}Configuration: ${CONFIGURATION}${NC}"
echo -e "${YELLOW}Runtime: ${RUNTIME}${NC}"
echo -e "${YELLOW}Self-Contained: ${SELF_CONTAINED}${NC}"
echo -e "${YELLOW}Single File: ${SINGLE_FILE}${NC}"
echo ""

# Project details
PROJECT_FILE="gspro-r10.csproj"
OUTPUT_DIR="bin/${CONFIGURATION}/publish/${RUNTIME}"

# Clean previous build
echo -e "${GREEN}Cleaning previous build...${NC}"
if [ -d "$OUTPUT_DIR" ]; then
    rm -rf "$OUTPUT_DIR"
fi

# Build arguments
BUILD_ARGS=(
    "publish"
    "$PROJECT_FILE"
    "-c" "$CONFIGURATION"
    "-r" "$RUNTIME"
    "--self-contained" "$SELF_CONTAINED"
)

if [ "$SINGLE_FILE" = "true" ]; then
    BUILD_ARGS+=("-p:PublishSingleFile=true")
    BUILD_ARGS+=("-p:IncludeNativeLibrariesForSelfExtract=true")
fi

# Additional optimizations for Release builds
if [ "$CONFIGURATION" = "Release" ]; then
    BUILD_ARGS+=("-p:PublishTrimmed=false")  # Disable trimming to avoid runtime issues
    BUILD_ARGS+=("-p:DebugType=none")
    BUILD_ARGS+=("-p:DebugSymbols=false")
fi

# Execute build
echo -e "${GREEN}Building project...${NC}"
echo -e "\033[0;90mCommand: dotnet ${BUILD_ARGS[*]}${NC}"
echo ""

if dotnet "${BUILD_ARGS[@]}"; then
    echo ""
    echo -e "${GREEN}======================================${NC}"
    echo -e "${GREEN}Build completed successfully!${NC}"
    echo -e "${GREEN}======================================${NC}"
    echo ""
    echo -e "${YELLOW}Output location: ${OUTPUT_DIR}${NC}"
    echo ""

    # List output files
    if [ -d "$OUTPUT_DIR" ]; then
        echo -e "${YELLOW}Output files:${NC}"
        find "$OUTPUT_DIR" -maxdepth 1 -type f | while read -r file; do
            filename=$(basename "$file")
            size=$(du -h "$file" | cut -f1)
            echo -e "  - ${filename} (${size})"
        done

        # Make executable if on Linux/Mac
        if [[ "$OSTYPE" == "linux-gnu"* ]] || [[ "$OSTYPE" == "darwin"* ]]; then
            echo ""
            echo -e "${GREEN}Making binaries executable...${NC}"
            chmod +x "${OUTPUT_DIR}"/gspro-r10* 2>/dev/null || true
        fi
    fi
else
    echo ""
    echo -e "${RED}======================================${NC}"
    echo -e "${RED}Build failed!${NC}"
    echo -e "${RED}======================================${NC}"
    exit 1
fi
