use leptos::prelude::*;
use leptos_meta::{provide_meta_context, HashedStylesheet, MetaTags, Title};
use leptos_router::{
    components::{Route, Router, Routes},
    ParamSegment, StaticSegment,
};

use crate::components::layout::AppLayout;
use crate::pages::dashboard::DashboardPage;
use crate::pages::server_detail::ServerDetailPage;
use crate::pages::settings::SettingsPage;

pub fn shell(options: LeptosOptions) -> impl IntoView {
    view! {
        <!DOCTYPE html>
        <html lang="en">
            <head>
                <meta charset="utf-8"/>
                <meta name="viewport" content="width=device-width, initial-scale=1"/>
                <AutoReload options=options.clone() />
                <HydrationScripts options=options.clone() />
                <MetaTags/>
                <HashedStylesheet options />
            </head>
            <body class="bg-gray-950 text-gray-100 min-h-screen">
                <App/>
            </body>
        </html>
    }
}

#[component]
pub fn App() -> impl IntoView {
    provide_meta_context();

    view! {
        <Title text="MCP Proxy"/>

        <Router>
            <Routes fallback=|| "Page not found.".into_view()>
                <Route path=StaticSegment("") view=|| view! {
                    <AppLayout>
                        <DashboardPage/>
                    </AppLayout>
                }/>
                <Route path=StaticSegment("settings") view=|| view! {
                    <AppLayout>
                        <SettingsPage/>
                    </AppLayout>
                }/>
                <Route path=(StaticSegment("servers"), ParamSegment("slug")) view=|| view! {
                    <AppLayout>
                        <ServerDetailPage/>
                    </AppLayout>
                }/>
            </Routes>
        </Router>
    }
}
