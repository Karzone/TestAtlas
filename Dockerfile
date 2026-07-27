# TestAtlas MCP server — serves a queryable, semantic map of a .NET
# test-automation solution over stdio (JSON-RPC / Model Context Protocol).
#
# This image installs the published .NET tools from NuGet and indexes the
# bundled SampleShop sample into a map, so the server starts and answers an
# introspection request (initialize + tools/list) out of the box — which is
# what MCP directory checks (e.g. Glama) verify.
#
# Build:  docker build -t testatlas-mcp .
# Run:    docker run -i --rm testatlas-mcp                 # serves the SampleShop map
#         docker run -i --rm -v "$PWD/codemap.db:/map.db" testatlas-mcp /map.db   # your own map
FROM mcr.microsoft.com/dotnet/sdk:8.0

# CLI builds the map; MCP server serves it. Both are .NET global tools on NuGet.
RUN dotnet tool install --global TestAtlas.Cli \
 && dotnet tool install --global TestAtlas.Mcp
ENV PATH="${PATH}:/root/.dotnet/tools"

WORKDIR /app

# Index the bundled sample solution into a map. TestAtlas is a syntax-only pass,
# so the sample needs no NuGet restore or compilation.
COPY samples/SampleShop ./SampleShop
RUN testatlas index ./SampleShop/SampleShop.sln --output /app/codemap.db

# Speak MCP over stdio against the map. An MCP client (or Glama) connects here.
ENTRYPOINT ["testatlas-mcp", "/app/codemap.db"]
