// vitest.config.ts
import { defineConfig } from "file:///D:/Source/repos/LmDotnetTools/samples/LmStreaming.Sample/ClientApp/node_modules/vitest/dist/config.js";
import vue from "file:///D:/Source/repos/LmDotnetTools/samples/LmStreaming.Sample/ClientApp/node_modules/@vitejs/plugin-vue/dist/index.mjs";
import { resolve } from "path";
var __vite_injected_original_dirname = "D:\\Source\\repos\\LmDotnetTools\\samples\\LmStreaming.Sample\\ClientApp";
var vitest_config_default = defineConfig({
  plugins: [vue()],
  test: {
    environment: "happy-dom",
    globals: true,
    include: ["src/**/*.test.ts"],
    coverage: {
      provider: "v8",
      reporter: ["text", "json", "html"],
      include: ["src/**/*.ts", "src/**/*.vue"],
      exclude: ["src/**/*.test.ts", "src/__tests__/**"]
    }
  },
  resolve: {
    alias: {
      "@": resolve(__vite_injected_original_dirname, "./src")
    }
  }
});
export {
  vitest_config_default as default
};
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsidml0ZXN0LmNvbmZpZy50cyJdLAogICJzb3VyY2VzQ29udGVudCI6IFsiY29uc3QgX192aXRlX2luamVjdGVkX29yaWdpbmFsX2Rpcm5hbWUgPSBcIkQ6XFxcXFNvdXJjZVxcXFxyZXBvc1xcXFxMbURvdG5ldFRvb2xzXFxcXHNhbXBsZXNcXFxcTG1TdHJlYW1pbmcuU2FtcGxlXFxcXENsaWVudEFwcFwiO2NvbnN0IF9fdml0ZV9pbmplY3RlZF9vcmlnaW5hbF9maWxlbmFtZSA9IFwiRDpcXFxcU291cmNlXFxcXHJlcG9zXFxcXExtRG90bmV0VG9vbHNcXFxcc2FtcGxlc1xcXFxMbVN0cmVhbWluZy5TYW1wbGVcXFxcQ2xpZW50QXBwXFxcXHZpdGVzdC5jb25maWcudHNcIjtjb25zdCBfX3ZpdGVfaW5qZWN0ZWRfb3JpZ2luYWxfaW1wb3J0X21ldGFfdXJsID0gXCJmaWxlOi8vL0Q6L1NvdXJjZS9yZXBvcy9MbURvdG5ldFRvb2xzL3NhbXBsZXMvTG1TdHJlYW1pbmcuU2FtcGxlL0NsaWVudEFwcC92aXRlc3QuY29uZmlnLnRzXCI7aW1wb3J0IHsgZGVmaW5lQ29uZmlnIH0gZnJvbSAndml0ZXN0L2NvbmZpZyc7XHJcbmltcG9ydCB2dWUgZnJvbSAnQHZpdGVqcy9wbHVnaW4tdnVlJztcclxuaW1wb3J0IHsgcmVzb2x2ZSB9IGZyb20gJ3BhdGgnO1xyXG5cclxuZXhwb3J0IGRlZmF1bHQgZGVmaW5lQ29uZmlnKHtcclxuICBwbHVnaW5zOiBbdnVlKCldLFxyXG4gIHRlc3Q6IHtcclxuICAgIGVudmlyb25tZW50OiAnaGFwcHktZG9tJyxcclxuICAgIGdsb2JhbHM6IHRydWUsXHJcbiAgICBpbmNsdWRlOiBbJ3NyYy8qKi8qLnRlc3QudHMnXSxcclxuICAgIGNvdmVyYWdlOiB7XHJcbiAgICAgIHByb3ZpZGVyOiAndjgnLFxyXG4gICAgICByZXBvcnRlcjogWyd0ZXh0JywgJ2pzb24nLCAnaHRtbCddLFxyXG4gICAgICBpbmNsdWRlOiBbJ3NyYy8qKi8qLnRzJywgJ3NyYy8qKi8qLnZ1ZSddLFxyXG4gICAgICBleGNsdWRlOiBbJ3NyYy8qKi8qLnRlc3QudHMnLCAnc3JjL19fdGVzdHNfXy8qKiddLFxyXG4gICAgfSxcclxuICB9LFxyXG4gIHJlc29sdmU6IHtcclxuICAgIGFsaWFzOiB7XHJcbiAgICAgICdAJzogcmVzb2x2ZShfX2Rpcm5hbWUsICcuL3NyYycpLFxyXG4gICAgfSxcclxuICB9LFxyXG59KTtcclxuIl0sCiAgIm1hcHBpbmdzIjogIjtBQUEwWSxTQUFTLG9CQUFvQjtBQUN2YSxPQUFPLFNBQVM7QUFDaEIsU0FBUyxlQUFlO0FBRnhCLElBQU0sbUNBQW1DO0FBSXpDLElBQU8sd0JBQVEsYUFBYTtBQUFBLEVBQzFCLFNBQVMsQ0FBQyxJQUFJLENBQUM7QUFBQSxFQUNmLE1BQU07QUFBQSxJQUNKLGFBQWE7QUFBQSxJQUNiLFNBQVM7QUFBQSxJQUNULFNBQVMsQ0FBQyxrQkFBa0I7QUFBQSxJQUM1QixVQUFVO0FBQUEsTUFDUixVQUFVO0FBQUEsTUFDVixVQUFVLENBQUMsUUFBUSxRQUFRLE1BQU07QUFBQSxNQUNqQyxTQUFTLENBQUMsZUFBZSxjQUFjO0FBQUEsTUFDdkMsU0FBUyxDQUFDLG9CQUFvQixrQkFBa0I7QUFBQSxJQUNsRDtBQUFBLEVBQ0Y7QUFBQSxFQUNBLFNBQVM7QUFBQSxJQUNQLE9BQU87QUFBQSxNQUNMLEtBQUssUUFBUSxrQ0FBVyxPQUFPO0FBQUEsSUFDakM7QUFBQSxFQUNGO0FBQ0YsQ0FBQzsiLAogICJuYW1lcyI6IFtdCn0K
