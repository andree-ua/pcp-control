import { NextResponse, type NextRequest } from "next/server";

/**
 * Redirección temprana basada en la cookie marcadora (no contiene el JWT).
 * La validación real la hace /api/auth/me a través de AuthGate.
 */
export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  const hasMarker = request.cookies.get("pcp_session")?.value === "1";

  if (pathname === "/login") {
    if (hasMarker) {
      return NextResponse.redirect(new URL("/", request.url));
    }
    return NextResponse.next();
  }

  if (!hasMarker) {
    const loginUrl = new URL("/login", request.url);
    return NextResponse.redirect(loginUrl);
  }

  return NextResponse.next();
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};
