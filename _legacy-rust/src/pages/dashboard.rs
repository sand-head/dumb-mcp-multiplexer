use leptos::prelude::*;

use crate::components::server_form::ServerForm;
use crate::types::McpServer;

/// Fetch all configured servers from the database.
#[server]
pub async fn get_servers() -> Result<Vec<McpServer>, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    db::get_all_servers(pool)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

/// Toggle server enabled state (from dashboard quick action).
#[server]
pub async fn toggle_server_enabled(id: String) -> Result<bool, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    db::toggle_server_enabled(pool, &id)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

#[component]
pub fn DashboardPage() -> impl IntoView {
    let servers = Resource::new(|| (), |_| get_servers());
    let (show_add_form, set_show_add_form) = signal(false);

    view! {
        <div>
            <div class="flex items-center justify-between mb-6">
                <div>
                    <h2 class="text-2xl font-bold text-white">"Upstream Servers"</h2>
                    <p class="text-sm text-gray-400 mt-1">
                        "Configure MCP servers to aggregate under a single endpoint."
                    </p>
                </div>
                <button
                    on:click=move |_| set_show_add_form.set(true)
                    class="px-4 py-2 text-sm bg-indigo-600 hover:bg-indigo-500 text-white rounded-lg font-medium transition-colors"
                >
                    "+ Add Server"
                </button>
            </div>

            <Suspense fallback=move || view! {
                <div class="text-gray-400">"Loading servers..."</div>
            }>
                {move || Suspend::new(async move {
                    match servers.await {
                        Ok(servers) if servers.is_empty() => {
                            view! {
                                <div class="border border-dashed border-gray-700 rounded-xl p-12 text-center">
                                    <div class="text-gray-500 text-4xl mb-4">"📡"</div>
                                    <h3 class="text-lg font-medium text-gray-300 mb-2">
                                        "No servers configured"
                                    </h3>
                                    <p class="text-sm text-gray-500 mb-4">
                                        "Add an upstream MCP server to get started."
                                    </p>
                                    <button
                                        on:click=move |_| set_show_add_form.set(true)
                                        class="px-4 py-2 text-sm bg-indigo-600 hover:bg-indigo-500 text-white rounded-lg font-medium transition-colors"
                                    >
                                        "+ Add Server"
                                    </button>
                                </div>
                            }.into_any()
                        }
                        Ok(servers) => {
                            view! {
                                <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                                    {servers.into_iter().map(|server| {
                                        let slug = server.slug.clone();
                                        let href = format!("/servers/{}", slug);
                                        view! {
                                            <a href=href class="block bg-gray-900 border border-gray-800 rounded-xl p-5 hover:border-gray-700 transition-colors group">
                                                <div class="flex items-start justify-between mb-3">
                                                    <div>
                                                        <h3 class="font-semibold text-white group-hover:text-indigo-300 transition-colors">
                                                            {server.name.clone()}
                                                        </h3>
                                                        <p class="text-xs text-gray-500 font-mono">
                                                            {server.slug.clone()}
                                                        </p>
                                                    </div>
                                                    <span class={if server.enabled {
                                                        "inline-block w-2.5 h-2.5 rounded-full bg-emerald-400"
                                                    } else {
                                                        "inline-block w-2.5 h-2.5 rounded-full bg-gray-600"
                                                    }}></span>
                                                </div>
                                                <p class="text-sm text-gray-400 truncate">
                                                    {server.url.unwrap_or_else(|| "—".to_string())}
                                                </p>
                                            </a>
                                        }
                                    }).collect_view()}
                                </div>
                            }.into_any()
                        }
                        Err(e) => {
                            view! {
                                <div class="text-red-400">
                                    {format!("Error loading servers: {e}")}
                                </div>
                            }.into_any()
                        }
                    }
                })}
            </Suspense>

            // Add Server modal
            {move || {
                if show_add_form.get() {
                    view! {
                        <ServerForm
                            on_saved=move |_| {
                                set_show_add_form.set(false);
                                servers.refetch();
                            }
                            on_cancel=move |_| set_show_add_form.set(false)
                        />
                    }.into_any()
                } else {
                    ().into_any()
                }
            }}
        </div>
    }
}
