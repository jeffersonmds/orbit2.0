using System;
using System.Windows.Media;

namespace Orbit.Services;

// Anel de progresso: o contorno nativo de uma Ellipse cortado via StrokeDashArray
// (no 1.0, Path+ArcSegment perto de 360° renderizava facetado nesta máquina).
// Valores de StrokeDashArray são múltiplos da espessura do traço (convenção do WPF).
// Na view, a Ellipse leva RotateTransform -90 pra começar no topo.
public static class Ring
{
    public static DoubleCollection DashArray(double progress, double diameter, double strokeThickness)
    {
        double p = Math.Clamp(progress, 0.0, 1.0);
        double circumference = Math.PI * diameter / strokeThickness;
        return new DoubleCollection { p * circumference, (1 - p) * circumference };
    }
}
