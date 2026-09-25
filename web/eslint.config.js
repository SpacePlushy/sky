// ESLint flat config: the recommended JavaScript rules plus typescript-eslint's strict,
// type-aware rules over the TypeScript sources. `npm run lint` allows no warnings.
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import globals from "globals";
import tseslint from "typescript-eslint";

export default defineConfig(
    globalIgnores(["dist/", "node_modules/", "coverage/"]),
    js.configs.recommended,
    tseslint.configs.strictTypeChecked,
    tseslint.configs.stylisticTypeChecked,
    {
        languageOptions: {
            globals: globals.browser,
            parserOptions: {
                projectService: true,
                tsconfigRootDir: import.meta.dirname,
            },
        },
        rules: {
            // Numbers in template strings are the normal way to build SVG path data and labels.
            "@typescript-eslint/restrict-template-expressions": ["error", { allowNumber: true }],
            eqeqeq: "error",
            "no-console": "error",
        },
    },
    {
        // This file is plain JavaScript outside the TypeScript project.
        files: ["**/*.js"],
        extends: [tseslint.configs.disableTypeChecked],
        languageOptions: { globals: globals.node },
    },
);
