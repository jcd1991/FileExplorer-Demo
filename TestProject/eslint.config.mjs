import js from "@eslint/js";
import globals from "globals";

export default [
  {
    ignores: [
      "**/node_modules/**",
      "**/bin/**",
      "**/obj/**",
      "**/playwright-report/**",
      "**/test-results/**",
    ],
  },
  js.configs.recommended,
  {
    files: ["wwwroot/js/**/*.js"],
    languageOptions: { globals: globals.browser },
  },
  {
    files: ["performance/k6/**/*.js"],
    languageOptions: {
      globals: { __ENV: "readonly", console: "readonly" },
    },
  },
  {
    files: ["browser-tests/**/*.{js,cjs,mjs}"],
    languageOptions: {
      sourceType: "commonjs",
      globals: globals.node,
    },
  },
];
