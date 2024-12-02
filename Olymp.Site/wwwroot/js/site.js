// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

function updateTheme() {
    const colorMode = window.matchMedia("(prefers-color-scheme: dark)").matches ?
        "dark" :
        "light";
    document.querySelector("html").setAttribute("data-bs-theme", colorMode);
}

updateTheme();
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', updateTheme);
